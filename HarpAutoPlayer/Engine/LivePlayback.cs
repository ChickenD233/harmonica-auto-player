using HarpAutoPlayer.Input;
using HarpAutoPlayer.Midi;

namespace HarpAutoPlayer.Engine;

/// <summary>口琴上的一个动作：音键本身，或三个修饰键。</summary>
public enum LiveKey
{
    Z, X, C, V, B, N, M, Comma,        // 音键：do re mi fa sol la si 高音do
    MouseLeft,                          // 降一个八度
    MouseRight,                         // 升一个八度
    MouseSharp                          // 升半音
}

/// <summary>一次映射结果：MIDI 音高 → 口琴动作。</summary>
public readonly record struct LiveMapping(
    bool Playable, LiveKey Key, bool Low, bool High, bool Sharp, string Reason);

/// <summary>
/// MIDI 设备实时转键盘：把设备送来的音符立刻变成游戏能读到的按键。
///
/// 与 <see cref="PlaybackEngine"/> 的分工：
/// - 文件播放：整首谱面提前算好事件表，按音乐时间派发。
/// - 设备实时：来一个音发一个音，只留修饰键提前量。
///
/// 时序规则沿用 <see cref="InputTiming"/> 的物理毫秒：
/// 修饰键比音键早 ModLeadMs；每次按下至少跨过一个帧点（否则游戏按帧采样时读不到）；
/// 同一根音键上前后两个音必须隔开 RetriggerMs。
/// 口琴是单音乐器，同一个键上已经在响的音会被后来的音换掉。
/// </summary>
public sealed class LivePlayback : IDisposable
{
    private const int MaxQueue = 1024;          // 事件队列上限，防止异常情况下无限增长
    private const int MaxSounding = 32;         // 同时"在响"的音符上限
    private const int MaxRecent = 24;           // 自动贴合的样本数

    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly List<Pending> _queue = new();          // 按 Due 升序
    private readonly List<Sounding> _sounding = new();      // 按按下顺序
    private readonly List<int> _recent = new();
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>一个待发出的动作。</summary>
    private sealed class Pending
    {
        public double Due;                 // 到点时刻（Environment.TickCount64 毫秒）
        public LiveKey Key;
        public bool Down;
    }

    /// <summary>一个已经按下（或已排定按下）的音符。</summary>
    private sealed class Sounding
    {
        public int Pitch;
        public LiveKey Key;
        public double DownAt;              // 排定的按下时刻
        public double MinUpAt;             // 最早可抬起时刻（保证游戏采样到）
        public bool Released;              // 是否已排定抬起
    }

    /// <summary>
    /// 输出口。默认直接调 <see cref="InputSender"/>；时序回归测试换成记录器，
    /// 就能在没有游戏、也不真的动键盘的情况下检查整条时间线。
    /// </summary>
    public Action<LiveKey, bool> Sink { get; set; } = DefaultSink;

    private static void DefaultSink(LiveKey key, bool down)
    {
        switch (key)
        {
            case LiveKey.MouseLeft:
                if (down) InputSender.MouseDown(InputSender.MouseButton.Left);
                else InputSender.MouseUp(InputSender.MouseButton.Left);
                break;
            case LiveKey.MouseRight:
                if (down) InputSender.MouseDown(InputSender.MouseButton.Right);
                else InputSender.MouseUp(InputSender.MouseButton.Right);
                break;
            case LiveKey.MouseSharp:
                if (down) InputSender.MouseDown(InputSender.MouseButton.Middle);
                else InputSender.MouseUp(InputSender.MouseButton.Middle);
                break;
            case LiveKey.Comma:
                if (down) InputSender.KeyDown(','); else InputSender.KeyUp(',');
                break;
            default:
                char c = (char)('Z' + (int)key);
                if (down) InputSender.KeyDown(c); else InputSender.KeyUp(c);
                break;
        }
    }

    // —— 修饰键状态机（锁内访问）——
    private LiveKey? _modKey;              // 当前按住的八度修饰键（MouseLeft / MouseRight）
    private bool _modSharp;                // 当前是否按着中键（升半音）

    // —— 输出时间线游标（锁内访问）——
    private double _lastDownAt = double.NegativeInfinity;   // 最后一次音键按下时刻
    private double _lastKeyUpAt = double.NegativeInfinity;  // 最后一次音键抬起时刻
    private double _lastUpAt = double.NegativeInfinity;     // 最后一次抬起时刻（音键或修饰键）

    public event Action<string>? Log;
    public event Action<LiveMapping, int, int>? NoteObserved;   // 映射结果、原始音高、力度

    /// <summary>设备发来的音符数（note on）。</summary>
    public int NoteOnCount { get; private set; }
    /// <summary>超出可演奏音域、没有发声的音符数。</summary>
    public int OutOfRangeCount { get; private set; }
    /// <summary>弹得太短（按下与抬起落在同一帧），被自动补到最短按住的次数。</summary>
    public int TooShortCount { get; private set; }
    /// <summary>同一根音键上被后来音符顶掉的次数。</summary>
    public int StolenCount { get; private set; }

    /// <summary>移调（半音，-24..+24）。</summary>
    public int Transpose
    {
        get => _transpose;
        set => _transpose = Math.Clamp(value, -24, 24);
    }
    private int _transpose;

    /// <summary>基准八度（MIDI 八度编号，C4 = 4）。口琴从这个八度的 do 吹到高一个八度的 do。</summary>
    public int BaseOctave
    {
        get => _baseOctave;
        set => _baseOctave = Math.Clamp(value, 0, 8);
    }
    private int _baseOctave = 4;

    /// <summary>力度低于此值的音符忽略（1..127）。设备抖动或触后噪声大时调高。</summary>
    public int MinVelocity
    {
        get => _minVelocity;
        set => _minVelocity = Math.Clamp(value, 1, 127);
    }
    private int _minVelocity = 1;

    /// <summary>输入时序预算（物理毫秒），与文件播放共用一套档位。</summary>
    public InputTiming Timing { get; set; } = InputTiming.Standard;

    /// <summary>自动贴合音域：按最近弹过的音调整基准八度，让跨八度的设备也能吹全。</summary>
    public bool AutoFit { get; set; } = true;

    /// <summary>
    /// 音高 → 口琴动作。基准八度 b 的 do..si 是 z..m；
    /// 高一个八度按右键，低一个八度按左键；升半音按中键；
    /// 最高只能到"高高音 do / #do"（右键 + 逗号，可再加中键）。
    /// </summary>
    public static LiveMapping Map(int pitch, int baseOctave)
    {
        int pc = Music.Mod(pitch, 12);
        int oct = pitch / 12 - 1;
        int d = oct - baseOctave;

        bool sharp = NoteMapper.IsSharpPitch(pitch);
        int idx = NoteMapper.KeyOfPitch(pitch) switch
        {
            'Z' => 0, 'X' => 1, 'C' => 2, 'V' => 3, 'B' => 4, 'N' => 5, _ => 6   // do..si
        };

        if (d is >= -1 and <= 1)
            return new LiveMapping(true, (LiveKey)idx, d == -1, d == 1, sharp, "");
        if (d == 2 && pc is 0 or 1)
            return new LiveMapping(true, LiveKey.Comma, false, true, pc == 1, "");

        var (rlo, rhi) = PlayableRange(baseOctave);
        return new LiveMapping(false, LiveKey.Z, false, false, false,
            $"超出音域（本档可吹 {Music.NoteName(rlo)} ~ {Music.NoteName(rhi)}，可用基准八度或移调调整）");
    }

    /// <summary>
    /// 可演奏音高范围（含）。必须与 <see cref="Map"/> 的判定一致：
    /// 最低音 = 低音 do，最高音 = 高高音 #do（比基准八度高一个八度的 C 再加半音）。
    /// </summary>
    public static (int Lo, int Hi) PlayableRange(int baseOctave)
        => RangeCore(baseOctave);

    /// <summary>
    /// 范围计算的实现点。写成一个明确的偏移量，避免读者去数八度：
    /// 低音 do = 基准八度的 C；最高音 = 再高两个八度 + 一个半音（高高音 #do）。
    /// 基准 4 档因此是 48(C3) ~ 85(C#6)，与 <see cref="Map"/> 的判定完全一致。
    /// </summary>
    private static (int Lo, int Hi) RangeCore(int baseOctave)
    {
        const int TopOffset = 37;    // 3 个八度 + 1 个半音 = 36 + 1
        int low = baseOctave * 12;
        int high = baseOctave * 12 + TopOffset;
        return (low, high);
    }

    /// <summary>
    /// 从一组音高挑基准八度：先让"超出音域"的音最少，再让音域最贴合唱到的音
    /// （超出的音离音域越远扣分越多，这样每个音都尽量落在音域里）。
    /// 返回 (基准八度, 超出音域的音数)。
    /// </summary>
    public static (int BaseOctave, int OutCount) FitBaseOctave(IReadOnlyList<int> pitches)
    {
        if (pitches.Count == 0) return (4, 0);
        int best = 4, bestOut = int.MaxValue;
        double bestScore = double.MaxValue;
        for (int b = 0; b <= 8; b++)
        {
            var (rlo, rhi) = PlayableRange(b);
            int outCount = 0;
            double score = 0;
            foreach (int p in pitches)
            {
                if (p < rlo) { outCount++; score += (rlo - p) * (rlo - p) * 0.5; }
                else if (p > rhi) { outCount++; score += (p - rhi) * (p - rhi) * 0.5; }
                else score += CenterDistance(p, rlo, rhi);
            }
            if (outCount < bestOut || (outCount == bestOut && score < bestScore))
            {
                bestOut = outCount;
                bestScore = score;
                best = b;
            }
        }
        return (Math.Clamp(best, 0, 8), bestOut);
    }

    /// <summary>音符到音域中心的距离：让音域尽量"套住"这批音，而不是贴在边上。</summary>
    private static double CenterDistance(int pitch, int lo, int hi)
    {
        double center = (lo + hi) / 2.0;
        double half = (hi - lo) / 2.0;
        double d = Math.Abs(pitch - center) / Math.Max(1.0, half);
        return d * d;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _queue.Clear();
            _sounding.Clear();
            _modKey = null;
            _modSharp = false;
            _lastDownAt = _lastKeyUpAt = _lastUpAt = double.NegativeInfinity;
            _thread = new Thread(Worker) { IsBackground = true, Name = "LivePlayback" };
            _thread.Start();
        }
    }

    /// <summary>停止并松开所有键/鼠标键。</summary>
    public void Stop()
    {
        bool wasRunning;
        lock (_gate)
        {
            wasRunning = _running;
            _running = false;
            _queue.Clear();
            _sounding.Clear();
            _modKey = null;
            _modSharp = false;
        }
        _wake.Release();
        if (_thread != null && _thread.IsAlive && !ReferenceEquals(_thread, Thread.CurrentThread))
            _thread.Join(500);
        _thread = null;
        if (wasRunning) InputSender.ReleaseEverything();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _wake.Dispose();
    }

    /// <summary>设备按下了一个音（设备线程调用）。</summary>
    public void NoteOn(int pitch, int velocity)
    {
        if (velocity < MinVelocity) return;
        if (pitch is < 0 or > 127) return;

        double now = NowMs();
        LiveMapping map;
        bool octaveMoved = false;
        lock (_gate)
        {
            NoteOnCount++;
            octaveMoved = ObserveForAutoFit(pitch);
            map = Map(pitch + _transpose, _baseOctave);
            if (!map.Playable) OutOfRangeCount++;
        }
        NoteObserved?.Invoke(map, pitch, velocity);

        if (octaveMoved)
            Log?.Invoke($"[MIDI] 自动贴合音域：基准八度改为 {Music.NoteName(_baseOctave * 12)}");
        if (!map.Playable)
        {
            Log?.Invoke($"[MIDI] {Music.NoteName(pitch)} 未发声：{map.Reason}");
            return;
        }

        lock (_gate)
        {
            // 同一个键上已经在响的音：先换掉（口琴一次只吹一个音，后来的优先）
            foreach (var s in _sounding.Where(x => !x.Released && x.Key == map.Key).ToList())
            {
                ReleaseNote(s, now, retrigger: true);
                StolenCount++;
            }

            // 前一个音（任意键）至少按住一帧，游戏才采样得到；同一根键还要跨过重触发间隔。
            double minDown = now;
            if (_lastDownAt > double.NegativeInfinity)
            {
                double need = _lastDownAt + minUpMs;
                if (map.Key == _lastKey && _lastKeyUpAt > double.NegativeInfinity)
                    need = Math.Max(need, _lastKeyUpAt + retrigMs);
                if (need > minDown) minDown = need;
            }

            // 修饰键切换：老状态立刻松开，新状态提前 modLead 按下
            double modLead = Math.Max(Timing.ModLeadMs, Timing.FrameMs);
            LiveKey? wantMod = map.Low ? LiveKey.MouseLeft : map.High ? LiveKey.MouseRight : null;
            if (_modKey != wantMod)
            {
                if (_modKey is LiveKey old) Enqueue(now, old, false);
                if (wantMod is LiveKey neu) Enqueue(Math.Max(now, minDown - modLead), neu, true);
                _modKey = wantMod;
            }
            if (_modSharp != map.Sharp)
            {
                if (_modSharp) Enqueue(now, LiveKey.MouseSharp, false);
                if (map.Sharp) Enqueue(Math.Max(now, minDown - modLead), LiveKey.MouseSharp, true);
                _modSharp = map.Sharp;
            }

            double downAt = minDown;
            var note = new Sounding
            {
                Pitch = pitch,
                Key = map.Key,
                DownAt = downAt,
                MinUpAt = downAt + minUpMs
            };
            _sounding.Add(note);
            if (_sounding.Count > MaxSounding) _sounding.RemoveAt(0);
            Enqueue(downAt, map.Key, true);
            _lastDownAt = downAt;
            _lastKey = map.Key;
            _wake.Release();
        }
    }

    private LiveKey? _lastKey;

    /// <summary>设备松开了一个音（设备线程调用）。</summary>
    public void NoteOff(int pitch)
    {
        double now = NowMs();
        lock (_gate)
        {
            foreach (var s in _sounding.Where(x => x.Pitch == pitch && !x.Released).ToList())
                ReleaseNote(s, now, retrigger: false);
            _wake.Release();
        }
    }

    /// <summary>设备被拔掉或出错：松开所有键。</summary>
    public void ReleaseAll()
    {
        double now = NowMs();
        lock (_gate)
        {
            foreach (var s in _sounding.Where(x => !x.Released).ToList()) ReleaseNote(s, now, retrigger: false);
            if (_modSharp) { Enqueue(now, LiveKey.MouseSharp, false); _modSharp = false; }
            if (_modKey is LiveKey m) { Enqueue(now, m, false); _modKey = null; }
            _wake.Release();
        }
    }

    /// <summary>
    /// 排定一个音的抬起：最早是 now，最晚要保证"按下至少跨过一个帧点"。
    /// 同一根音键上前一个音还要多留 retriggerMs，否则游戏会把两个音读成一个。
    /// </summary>
    private void ReleaseNote(Sounding s, double now, bool retrigger)
    {
        double due = Math.Max(now, s.MinUpAt);
        if (retrigger) due = Math.Max(due, s.DownAt + retrigMs);
        if (due > now + 0.5) TooShortCount++;
        s.Released = true;
        Enqueue(due, s.Key, false);
        if (due > _lastUpAt) _lastUpAt = due;
        if (s.Key == _lastKey && due > _lastKeyUpAt) _lastKeyUpAt = due;
    }

    private double minUpMs => Math.Max(Timing.MinHoldMs, Timing.FrameMs) + 1.0;
    private double retrigMs => Math.Max(Timing.RetriggerMs, Timing.FrameMs);

    private static double NowMs()
    {
#if HARP_TEST
        if (ClockForTest != null) return ClockForTest();
#endif
        return Environment.TickCount64;
    }

    /// <summary>插入队列并保持按 Due 升序（同刻事件保持插入顺序：先抬起，后按下）。</summary>
    private void Enqueue(double due, LiveKey key, bool down)
    {
        if (_queue.Count >= MaxQueue) _queue.RemoveAt(0);
        var ev = new Pending { Due = due, Key = key, Down = down };
        int i = _queue.Count;
        while (i > 0 && _queue[i - 1].Due > due) i--;
        _queue.Insert(i, ev);
    }

    // ================= 自动贴合音域 =================

    /// <summary>
    /// 记录一个原始音高（不含移调），样本够了就按它们调整基准八度。
    /// 返回 true 表示基准八度变了。
    /// </summary>
    private bool ObserveForAutoFit(int rawPitch)
    {
        _recent.Add(rawPitch);
        if (_recent.Count > MaxRecent) _recent.RemoveAt(0);
        if (!AutoFit || _recent.Count * 2 < MaxRecent) return false;

        var (oct, _) = FitBaseOctave(_recent.Select(p => p + _transpose).ToList());
        if (oct == _baseOctave) return false;
        _baseOctave = oct;
        return true;
    }

    // ================= 工作线程 =================

    private void Worker()
    {
        try
        {
            while (_running)
            {
                List<Pending>? due = null;
                lock (_gate)
                {
                    double now = NowMs();
                    if (_queue.Count > 0 && _queue[0].Due <= now)
                    {
                        due = new List<Pending>();
                        while (_queue.Count > 0 && _queue[0].Due <= now)
                        {
                            due.Add(_queue[0]);
                            _queue.RemoveAt(0);
                        }
                        _sounding.RemoveAll(s => s.Released && s.MinUpAt <= now);
                    }
                }

                if (due != null)
                {
                    var sink = Sink;
                    foreach (var e in due) sink(e.Key, e.Down);
                    continue;
                }

                int waitMs;
                lock (_gate)
                {
                    waitMs = _queue.Count == 0 ? 40 : (int)Math.Clamp(_queue[0].Due - NowMs(), 1, 20);
                }
                _wake.Wait(waitMs);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[MIDI] 实时演奏线程异常：{ex.Message}");
        }
        finally
        {
            _running = false;
            InputSender.ReleaseEverything();
        }
    }

#if HARP_TEST
    /// <summary>仅供时序回归测试：把队列里的事件一次性算出来（不真的发按键）。</summary>
    public (double Ms, LiveKey Key, bool Down)[] DrainForTest()
        => _queue.OrderBy(e => e.Due)
                 .Select(e => (e.Due, e.Key, e.Down))
                 .ToArray();

    /// <summary>仅供时序回归测试：可替换的时钟（毫秒）。</summary>
    public static Func<double>? ClockForTest;
#endif
}
