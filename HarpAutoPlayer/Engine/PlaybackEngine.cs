using System.Diagnostics;
using HarpAutoPlayer.Input;

namespace HarpAutoPlayer.Engine;

/// <summary>
/// 播放调度引擎：把映射后的音符变成键盘/鼠标按下抬起事件，后台线程按真实时间发送（SendInput）。
/// 时间模型：事件表用"音乐时间"（秒，与速度无关）；工作线程按速度把物理流逝时间积分成音乐时间，
/// 因此 Speed 随时可变、SeekFraction 可跳转、UpdateNotes 可播放中更换剩余音符（实时移调）。
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    // —— 对外事件（工作线程触发，UI 需自行 marshal）——
    public event Action<string>? Log;
    public event Action<string>? CurrentNoteChanged;   // 正在吹奏的音符描述（"" 表示无）
    public event Action? Finished;                     // 自然播完或手动停止

    /// <summary>
    /// 一个待派发的物理输入事件。T 为**音乐时间**（秒，与速度无关）。
    /// 用可变类而非 record，是为了在派发时回填"真正发出的时刻"，供时序诊断使用。
    /// </summary>
    private sealed class PhysicalEvent
    {
        public double T;
        public readonly int Kind;
        public readonly char Code;
        public readonly bool Down;
        public readonly string Label;

        public PhysicalEvent(double t, int kind, char code, bool down, string label)
        {
            T = t; Kind = kind; Code = code; Down = down; Label = label;
        }
    }

    private const int K_Key = 0;
    private const int K_MouseLeft = 1;
    private const int K_MouseRight = 2;
    private const int K_MouseMiddle = 3;

    private readonly object _gate = new();
    private List<PhysicalEvent> _events = new();
    private List<MappedNote> _allNotes = new();   // 本轮全部可演奏音符（跳转/重建用）
    private double _totalMusic;          // 音乐时间总长
    private double _speed = 1.0;
    private double _leadSec;             // 提前量（物理秒，与速度无关）
    private bool _loop;

    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _paused;
    private readonly ManualResetEventSlim _resumeGate = new(true);
    private readonly ManualResetEventSlim _cancelEvent = new(false);   // 停止信号，唤醒一切等待
    private readonly Stopwatch _clock = new();

    // 工作线程运行状态
    private double _musicNow;            // 已到达的音乐时间
    private double _lastPhys;            // 上一次积分采样的物理时刻
    private int _nextIdx;                // 下一个待派发事件下标

    private int _loopCount;
    private bool _manualStop;
    private string _snapNote = "";

    // UI 轮询快照（plain double 在 x64 上读写原子，轻微时序可接受）
    private double _snapElapsed;
    private double _snapTotal;
    private volatile int _snapLoop;

    public double ElapsedSeconds => _snapElapsed;
    public double TotalSeconds => _snapTotal;
    public int LoopCount => _snapLoop;
    public string CurrentNote => _snapNote;
    public bool IsRunning => _running;
    public bool IsPaused => _paused;

    /// <summary>实时播放速度（0.1–5.0，播放中可改，立即生效）。</summary>
    public double Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 0.1, 5.0);
    }

    /// <summary>输入时序预算（物理毫秒）。播放中可改，下一轮播放生效。</summary>
    public InputTiming Timing { get; set; } = InputTiming.Standard;

#if HARP_TEST
    /// <summary>
    /// 仅供时序回归测试使用：按指定预算构建事件表。
    /// 注意用局部副本设置 Timing，避免副作用污染引擎状态。
    /// </summary>
    public (double T, int Kind, char Code, bool Down)[] BuildScheduleForTest(
        IReadOnlyList<MappedNote> notes, InputTiming timing)
    {
        var saved = Timing;
        try
        {
            Timing = timing;
            var (evs, _) = BuildSchedule(notes, ModState.None);
            return evs.Select(e => (e.T, e.Kind, e.Code, e.Down)).ToArray();
        }
        finally { Timing = saved; }
    }

    /// <summary>仅供时序回归测试使用：调度决策追踪输出口（null = 不追踪）。</summary>
    public static Action<string>? TraceSink;
#endif

    /// <summary>本轮播放的输入时序诊断（由 Execute 在派发时统计）。</summary>
    public InputTimingProbe Probe { get; } = new();

    /// <summary>
    /// 本轮"没能成音"的音符数：按键时值被压到一个帧点都盖不住，游戏读不到。
    /// 修复后正常曲子应为 0。不是 0 就说明这首谱面挤得比输入档位允许的最快速度还快，
    /// 用户可以换「稳健」档或把速度降一点。
    /// </summary>
    public int SqueezedNotes => Probe.MinUpLimited;

    /// <summary>对外暴露的调度事件（音乐时间，秒）。用于导出按键表/宏，保证与实际演奏一致。</summary>
    public sealed record ScheduledEvent(double MusicTime, string Kind, char Key, bool Down)
    {
        /// <summary>便于人读的按键名（逗号显示为「,」）。</summary>
        public string KeyLabel => Key == ' ' ? "" : (Key == ',' ? "," : Key.ToString());
    }

    /// <summary>按指定时序预算构建"整首曲子"的事件表（不实际发送），与 <see cref="Play"/> 同一套构建逻辑。</summary>
    public static List<ScheduledEvent> BuildSchedulePreview(
        IReadOnlyList<MappedNote> notes, InputTiming? timing = null, double speed = 1.0)
    {
        var engine = new PlaybackEngine { Timing = timing ?? InputTiming.Standard };
        var inRange = notes.Where(n => n.InRange).ToList();
        var (evs, _) = engine.BuildSchedule(inRange, ModState.None);

        double safeSpeed = speed <= 0 ? 1.0 : Math.Clamp(speed, 0.1, 5.0);
        var list = new List<ScheduledEvent>(evs.Count);
        foreach (var e in evs)
        {
            string kind = e.Kind switch
            {
                K_Key => "key",
                K_MouseLeft => "mouse-left",
                K_MouseRight => "mouse-right",
                _ => "mouse-middle"
            };
            // 事件表用音乐时间；除以速度得到实际物理播放时刻（毫秒）
            list.Add(new ScheduledEvent(e.T / safeSpeed, kind, e.Code, e.Down));
        }
        return list;
    }

    /// <summary>开始播放。notes 为映射后 InRange 的音符（音乐时间，未乘速度）。</summary>
    public void Play(IReadOnlyList<MappedNote> notes, double speed, double leadMs, bool loop)
    {
        lock (_gate)
        {
            if (_running) StopInternal();

            _speed = speed <= 0 ? 1.0 : Math.Clamp(speed, 0.1, 5.0);
            // 提前量与速度无关：它是"提前多少物理时间发事件"，绝不能乘 speed
            // （旧版 5x 速度下会膨胀到 125ms，谱面与实际发声严重错位）；
            // 但又必须 ≥ ModLeadMs + 一帧，否则预置修饰键会被推到音键之后发出，音高全错。
            double needLeadMs = Timing.ModLeadMs + Timing.FrameMs;
            _leadSec = Math.Max(Timing.LeadMs, needLeadMs) / 1000.0;
            _loop = loop;
            _manualStop = false;
            _loopCount = 0;
            _snapNote = "";
            _snapLoop = 0;

            // 只保留可演奏音（跳转会重建整表，必须同样过滤）
            _allNotes = notes.Where(n => n.InRange).ToList();
            // 构建时以"当前真实按下的修饰键"为起点：上轮中断时若还按着鼠标键，
            // 先补发松开，避免游戏侧残留升半音/八度状态。
            (_events, _totalMusic) = BuildSchedule(_allNotes, CurrentModifiers());
            _snapTotal = _totalMusic;

            _musicNow = 0;
            _nextIdx = 0;

            Log?.Invoke($"开始播放：共 {_allNotes.Count} 个音符，总时长 ≈ {_totalMusic:F1}s" +
                        (_loop ? "（循环）" : "") +
                        $"；输入档位 {Timing.Name}（帧长 {Timing.FrameMs:F0}ms、修饰键提前 {Timing.ModLeadMs:F0}ms）");

            _running = true;
            _paused = false;
            _resumeGate.Set();
            _cancelEvent.Reset();
            _clock.Restart();
            _lastPhys = 0;
            _thread = new Thread(Worker) { IsBackground = true, Name = "PlaybackWorker" };
            _thread.Start();
        }
    }

    /// <summary>播放中更换剩余音符（用于实时移调）。位置保持在当前音乐时间附近。</summary>
    public void UpdateNotes(IReadOnlyList<MappedNote> notes)
    {
        lock (_gate)
        {
            if (!_running) return;

            // 必须先强制释放并同步状态机，否则换谱后的第一个音会以为修饰键还按着 → 音高错
            var startMods = ForceReleaseModifiers();

            var inRange = notes.Where(n => n.InRange).ToList();
            _allNotes = inRange;
            var (evs, total) = BuildSchedule(inRange, startMods);
            _events = evs;
            if (total > 0) _totalMusic = Math.Max(_totalMusic, total);
            _nextIdx = FindNextIdx(_musicNow);
            SetCurrentNote("");
            Log?.Invoke($"已应用移调：剩余可演奏 {inRange.Count} 音");
        }
    }

    /// <summary>跳转到总进度 0..1 的位置（播放中可用）。</summary>
    public void SeekFraction(double fraction)
    {
        lock (_gate)
        {
            if (!_running) return;

            var startMods = ForceReleaseModifiers();

            double target = Math.Clamp(fraction, 0, 1) * _totalMusic;
            // 从跳转点重新构建事件表：以当前（已全部松开的）修饰键状态为起点，
            // 保证跳转后的第一个音先建立正确的八度/升半音状态，音高不会错。
            (_events, _totalMusic) = BuildSchedule(_allNotes, startMods);
            _musicNow = target;
            _nextIdx = FindNextIdx(_musicNow);
            SetCurrentNote("");
            _snapElapsed = _musicNow;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_running || _paused) return;
            _paused = true;
            _resumeGate.Reset();
        }
        InputSender.ReleaseEverything();
        SetCurrentNote("");
        Log?.Invoke("已暂停（当前音中断）。");
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_running || !_paused) return;
            _paused = false;
            _resumeGate.Set();
        }
        Log?.Invoke("继续播放。");
    }

    /// <summary>停止并清理所有按下的键。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _manualStop = true;
            _running = false;
            _resumeGate.Set();
            _cancelEvent.Set();
        }
        InputSender.ReleaseEverything();
        SetCurrentNote("");
    }

    public void Dispose() => Stop();

    private void StopInternal()
    {
        _manualStop = true;
        _running = false;
        _resumeGate.Set();
        _cancelEvent.Set();
        InputSender.ReleaseEverything();
        SetCurrentNote("");
    }

    // ================= 后台线程 =================

    private void Worker()
    {
        try
        {
            while (_running)
            {
                if (_paused)
                {
                    _resumeGate.Wait();
                    _lastPhys = _clock.Elapsed.TotalSeconds;
                    continue;
                }

                // 1) 积分：物理流逝 × 速度 → 音乐时间
                double now = _clock.Elapsed.TotalSeconds;
                double dt = now - _lastPhys;
                _lastPhys = now;
                if (dt > 0) _musicNow += dt * _speed;

                // 2) 派发到期的事件
                // 提前量是**固定物理时间**（不乘速度）：否则 5x 时提前 125ms 发，
                // 谱面与实际发声严重错位。
                double lead = _leadSec;
                while (_running && _nextIdx < _events.Count && _events[_nextIdx].T <= _musicNow + lead)
                {
                    var ev = _events[_nextIdx];
                    Execute(ev);
                    if (ev.Kind == K_Key)
                    {
                        if (ev.Down) SetCurrentNote(ev.Label);
                        else if (_nextIdx + 1 >= _events.Count || _events[_nextIdx + 1].T > ev.T)
                            SetCurrentNote("");
                    }
                    _nextIdx++;
                }

                // 3) 循环 / 结束判定
                if (_nextIdx >= _events.Count)
                {
                    double slackT = _totalMusic + 0.25;
                    if (_musicNow < slackT)
                    {
                        SleepAWhile(slackT);
                        continue;
                    }
                    if (_loop)
                    {
                        RestartLoop();
                        continue;
                    }
                    break; // 自然播完
                }

                // 4) 进度快照（限频由外层节流，简单直接赋值即可）
                _snapElapsed = _musicNow;
                _snapTotal = _totalMusic;

                // 5) 睡到下一个事件（分段睡，保证速度调节平滑响应）
                double nextEventT = _events[_nextIdx].T;
                double wakeMusic = nextEventT - lead;
                if (_musicNow < wakeMusic)
                {
                    double remainMs = (wakeMusic - _musicNow) / _speed * 1000.0;
                    if (remainMs > 8)
                    {
                        _cancelEvent.Wait((int)Math.Min(remainMs - 4, 45));
                    }
                    else if (remainMs > 1.5)
                    {
                        _cancelEvent.Wait(1);
                    }
                    else
                    {
                        // 最后一点自旋等待，尽量精确
                        while (_running && !_paused && _musicNow < wakeMusic - 0.0004)
                        {
                            double t2 = _clock.Elapsed.TotalSeconds;
                            double d2 = t2 - _lastPhys;
                            _lastPhys = t2;
                            if (d2 > 0) _musicNow += d2 * _speed;
                        }
                    }
                }
            }
        }
        finally
        {
            _running = false;
            InputSender.ReleaseEverything();
            SetCurrentNote("");
            _snapElapsed = Math.Min(_snapElapsed, _totalMusic);

            if (!_manualStop)
                Finished?.Invoke();
            else
                _snapElapsed = 0;
        }
    }

    private void SleepAWhile(double untilMusic)
    {
        // 在直到 untilMusic 前保持积分前进（分段睡，可被停止信号打断）
        var sw = Stopwatch.StartNew();
        while (_running && !_paused && !_cancelEvent.IsSet &&
               _musicNow < untilMusic && sw.ElapsedMilliseconds < 60000)
        {
            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _lastPhys;
            _lastPhys = now;
            if (dt > 0) _musicNow += dt * _speed;
            _cancelEvent.Wait(2);
        }
    }

    private void RestartLoop()
    {
        InputSender.ReleaseEverything();
        _loopCount++;
        _snapLoop = _loopCount;
        _musicNow = 0;
        _nextIdx = 0;
        SetCurrentNote("");
        Log?.Invoke($"—— 第 {_loopCount + 1} 遍 ——");
        _cancelEvent.Wait(160);   // 中途点停止也能立刻醒来
        _lastPhys = _clock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// 定位"下一个待派发"事件：保留一段预滚窗口，让跳转/换谱后紧接的音符先重放它的八度/升半音修饰键。
    /// 窗口按**物理**时间预算折算成音乐时间（速度越快，同样物理时长覆盖的音乐时间越多）。
    /// </summary>
    private int FindNextIdx(double musicNow)
    {
        double physSec = (Math.Max(Timing.ModLeadMs, Timing.ReleaseGapMs) + Timing.FrameMs) / 1000.0;
        double floor = musicNow - physSec * _speed;
        int idx = 0;
        while (idx < _events.Count && _events[idx].T <= floor) idx++;
        return idx;
    }

    /// <summary>把物理事件真正发出去，并顺带记录时序诊断（发出时的音乐时间）。</summary>
    /// <summary>
    /// 试听模式：整个时间轴、倒计时、速度、循环、定位都照常走，但**不真的发按键/鼠标**。
    /// 界面改用内置 MIDI 合成器发声，于是"试听"就是放音频而不是在游戏里按键。
    /// 状态机仍然照常更新，切换到非试听时不会有残留状态。
    /// </summary>
    public bool Silent { get; set; }

    private void Execute(PhysicalEvent ev)
    {
        switch (ev.Kind)
        {
            case K_Key:
                if (ev.Down)
                {
                    if (!Silent) InputSender.KeyDown(ev.Code);
                    _physHeldKey = ev.Code;
                    Probe.OnNoteOn(ev.Code, ev.T);
                }
                else
                {
                    if (!Silent) InputSender.KeyUp(ev.Code);
                    if (_physHeldKey == ev.Code) _physHeldKey = '\0';
                    Probe.OnNoteOff(ev.Code, ev.T);
                }
                break;
            case K_MouseLeft:
                if (!Silent)
                {
                    if (ev.Down) InputSender.MouseDown(InputSender.MouseButton.Left);
                    else InputSender.MouseUp(InputSender.MouseButton.Left);
                }
                _physMods = _physMods with { L = ev.Down };
                Probe.OnModifier(ev.T, ev.Down);
                break;
            case K_MouseRight:
                if (!Silent)
                {
                    if (ev.Down) InputSender.MouseDown(InputSender.MouseButton.Right);
                    else InputSender.MouseUp(InputSender.MouseButton.Right);
                }
                _physMods = _physMods with { R = ev.Down };
                Probe.OnModifier(ev.T, ev.Down);
                break;
            case K_MouseMiddle:
                if (!Silent)
                {
                    if (ev.Down) InputSender.MouseDown(InputSender.MouseButton.Middle);
                    else InputSender.MouseUp(InputSender.MouseButton.Middle);
                }
                _physMods = _physMods with { M = ev.Down };
                Probe.OnModifier(ev.T, ev.Down);
                break;
        }
    }

    private void SetCurrentNote(string label)
    {
        _snapNote = label;
        CurrentNoteChanged?.Invoke(label);
    }

    // ================= 事件表构建（音乐时间） =================

    /// <summary>修饰键的真实按下状态（避免"我以为按着"与游戏实际状态不一致）。</summary>
    private readonly record struct ModState(bool L, bool R, bool M)
    {
        public static ModState None => new(false, false, false);
    }

    /// <summary>音键当前是否真的处于按下状态（供停止/跳转后与游戏对表）。</summary>
    private char _physHeldKey = '\0';
    private ModState _physMods = ModState.None;

    private ModState CurrentModifiers()
    {
        lock (_gate) return _physMods;
    }

    /// <summary>
    /// 强制把修饰键与音键全部松开，并把状态机复位。返回复位后的状态（全松）。
    /// 用于换谱/跳转：否则后面的音会以为修饰键还按着，导致音高错。
    /// </summary>
    private ModState ForceReleaseModifiers()
    {
        InputSender.ReleaseEverything();
        _physMods = ModState.None;
        _physHeldKey = '\0';
        Probe.Reset(0);
        return _physMods;
    }

    /// <summary>
    /// 把一个主旋律音符序列压成物理键盘/鼠标事件时间表（音乐时间，秒，与速度无关）。
    /// 旧实现把所有最小间隔写成 12ms / 8ms 这类远小于一帧的硬编码值，
    /// 把"松开前音 + 八度键 + 中键 + 本音按下"全挤在 20ms 内，游戏按帧采样时整簇被折叠、
    /// 排在末尾的音键被吃掉 → 漏音。本实现所有最小间隔改用 InputTiming 的**物理毫秒**：
    /// 修饰键比音键早 ModLeadMs 且音键至少晚一帧；同键两次按下 ≥ RetriggerMs；
    /// 每次按下至少按住一帧。
    ///
    /// 音符之间用**槽位**排开，而不是只靠"前音抬起 → 后音按下"的间隔：
    /// 与前音重叠（含同刻起音）的音顺延到前音之后，时值不变。口琴是单音乐器，
    /// 同刻起音本来只能吹响一个；靠"缩短前音"去腾位置，就会产生零时长按键 ——
    /// 游戏按帧采样时一帧都读不到，整段音被吃掉。
    /// </summary>
    private (List<PhysicalEvent>, double) BuildSchedule(
        IReadOnlyList<MappedNote> notes, ModState startMods)
    {
        var evs = new List<PhysicalEvent>();
        if (notes.Count == 0) return (evs, 0);

        var ordered = notes
            .OrderBy(n => n.Start)
            .ThenBy(n => n.End)
            .ToList();

        double frame = Timing.FrameMs / 1000.0;
        double modLead = Math.Max(Timing.ModLeadMs / 1000.0, frame);   // 修饰键至少提前一帧
        // 重触发间隔：至少要跨过"抬起被采样到"的那一帧，同时不小于配置值
        double retrig = Math.Max(Timing.RetriggerMs / 1000.0, frame);
        // 最短按住时刻：比一帧再多一点余量。
        // 只有刚好一帧时，若按下刚好落在帧边界上，整个按住区间可能一个帧点都不含 → 游戏读不到。
        // 加 1ms 余量后，区间内一定落得进至少一个帧点。
        double minUpT = frame + 0.001;

        // —— 修饰键状态机（起点 = 游戏侧当前真实状态）——
        Slot heldSlot = startMods.L ? Slot.Low : startMods.R ? Slot.High : Slot.Mid;
        bool heldSharp = startMods.M;

        // 起点若同时按着左右键（异常残留），先全部释放
        if (startMods.L && startMods.R)
        {
            evs.Add(new PhysicalEvent(0, K_MouseLeft, ' ', false, ""));
            evs.Add(new PhysicalEvent(0, K_MouseRight, ' ', false, ""));
            heldSlot = Slot.Mid;
        }

        // 起点若还按着音键（上一轮中断残留），先松开
        if (_physHeldKey != '\0')
        {
            evs.Add(new PhysicalEvent(0, K_Key, _physHeldKey, false, ""));
            _physHeldKey = '\0';
        }

        void EmitModifiers(bool wantL, bool wantR, bool wantM, double modT)
        {
            // 顺序固定：先松开所有不该按的（左、右、中），再按下所有该按的。
            // 这样即便同刻也不依赖排序稳定性，且半音切换时"松"先于"按"。
            if (heldSlot == Slot.Low && !wantL)
                evs.Add(new PhysicalEvent(modT, K_MouseLeft, ' ', false, ""));
            if (heldSlot == Slot.High && !wantR)
                evs.Add(new PhysicalEvent(modT, K_MouseRight, ' ', false, ""));
            if (heldSharp && !wantM)
                evs.Add(new PhysicalEvent(modT, K_MouseMiddle, ' ', false, ""));

            if (wantL && heldSlot != Slot.Low)
                evs.Add(new PhysicalEvent(modT, K_MouseLeft, ' ', true, ""));
            if (wantR && heldSlot != Slot.High)
                evs.Add(new PhysicalEvent(modT, K_MouseRight, ' ', true, ""));
            if (wantM && !heldSharp)
                evs.Add(new PhysicalEvent(modT, K_MouseMiddle, ' ', true, ""));
        }

        char? heldKey = null;
        double heldDownT = 0;      // 前音实际按下时刻
        double heldUpT = 0;        // 前音实际抬起时刻
        var lastDown = new Dictionary<char, double>();

        // 槽位起点：本音必须晚于上一个音（口琴是单音），槽位终点 = 前音实际抬起时刻。
        double slotStart = 0;

        foreach (var n in ordered)
        {
            double baseStart = Math.Max(0, n.Start);
            double endT = Math.Max(baseStart, n.End);      // 谱面结束时刻（保留原时值）
            double duration = endT - baseStart;

            // ① 本音最早能按下的时刻：
            //    - baseStart：谱面时刻（不与前音重叠时完全按原谱）
            //    - slotStart：前音实际抬起时刻（口琴是单音，重叠音必须排开）
            //    - 前音按下 + minUpT：保证前音能跨过一个帧点，被游戏采样到。
            //      少了这条，紧随其后的音就会把前音的时值压成 0 → 游戏整段读不到 → 漏音。
            double t = Math.Max(baseStart, slotStart);
            if (heldKey != null && t < heldDownT + minUpT) t = heldDownT + minUpT;
            endT = t + duration;

            bool wantL = n.OctaveSlot == Slot.Low;
            bool wantR = n.OctaveSlot == Slot.High;
            bool wantM = n.Sharp;

            // ② 同一根音键的重触发间隔（旧版只给 12ms，短于一帧 → 两音粘连）
            double downT = t;
            if (lastDown.TryGetValue(n.Key, out double prevDown) && downT < prevDown + retrig)
                downT = prevDown + retrig;

            // 若顺延已超过本音结束时刻，就按"本音可用时长"临时收敛重触发间隔：
            // 极快段落宁可留一点粘连风险，也不能把整段音推没（时值会归零）。
            if (lastDown.TryGetValue(n.Key, out prevDown) && downT > endT)
            {
                double avail = Math.Max(0, endT - prevDown);
                double effRetrig = Math.Max(frame, Math.Min(retrig, avail));
                downT = Math.Max(t, Math.Min(endT, prevDown + effRetrig));
            }

            // ③ 前音抬起：最早是它的谱面结束时刻，最晚是本音按下时刻。
            //    ①保证了这个区间至少跨过一个帧点，所以不会再出现 upT == downT 的零时长按键。
            if (heldKey is char prev)
            {
                double upT = Math.Min(heldUpT, downT);
                upT = Math.Min(downT, Math.Max(upT, heldDownT + minUpT));   // 至少跨一个帧点，且不越过本音
                if (upT < heldUpT - 1e-9) Probe.OnMinUpLimited();

                evs.Add(new PhysicalEvent(upT, K_Key, prev, false, ""));
                if (upT > slotStart) slotStart = upT;
                heldKey = null;
            }

            // ④ 修饰键切换：提前 modLead 发出，并保证音键至少晚于一帧
            if (wantL != (heldSlot == Slot.Low) ||
                wantR != (heldSlot == Slot.High) ||
                wantM != heldSharp)
            {
                double modT = Math.Max(0, downT - modLead);
                EmitModifiers(wantL, wantR, wantM, modT);
                heldSlot = wantL ? Slot.Low : wantR ? Slot.High : Slot.Mid;
                heldSharp = wantM;
                if (downT < modT + frame) downT = modT + frame;
            }

            // ⑤ 修饰键提前量若把本音按下推后了，整段跟着后移，时值不变。
            //    不能只推按下不推抬起：那会把时值压没，又变成零时长按键。
            if (downT > t)
            {
                double shift = downT - t;
                t += shift;
                endT += shift;
            }

            evs.Add(new PhysicalEvent(downT, K_Key, n.Key, true,
                NoteMapper.Describe(n, withTime: false)));
#if HARP_TEST
            TraceSink?.Invoke($"  音 {n.Key} 谱面 {baseStart:F4}→{baseStart + duration:F4} 槽位 {t:F4}→{endT:F4} "
                              + $"实发 down={downT:F4}"
                              + $"{(lastDown.ContainsKey(n.Key) ? $" prevDown={lastDown[n.Key]:F4}" : "")}");
#endif
            heldKey = n.Key;
            heldDownT = downT;
            heldUpT = endT;
            lastDown[n.Key] = downT;
        }

        if (heldKey is char last)
        {
            double upT = Math.Max(heldUpT, heldDownT + minUpT);
            evs.Add(new PhysicalEvent(upT, K_Key, last, false, ""));
        }

        for (int i = 0; i < evs.Count; i++)
        {
            if (evs[i].T < 0) evs[i].T = 0;
        }
        // 稳定排序：同刻事件保持"先修饰键、后音键"的插入顺序
        evs = evs.OrderBy(e => e.T).ToList();
        double total = evs.Count == 0 ? 0 : evs[^1].T;
        return (evs, total);
    }
}
