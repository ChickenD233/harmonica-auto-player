using System.Diagnostics;
using HarpAutoPlayer.Input;

namespace HarpAutoPlayer.Engine;

/// <summary>
/// 播放调度引擎：把映射后的音符变成一系列键盘/鼠标按下与抬起事件，
/// 后台线程按真实时间发送（SendInput）。
///
/// 时间模型：事件表使用“音乐时间”（单位：秒，与速度无关）。
/// 工作线程按 速度(speed) 把流逝的物理时间积分成音乐时间，
/// 因此 <see cref="Speed"/> 随时可变、<see cref="SeekFraction"/> 可跳转、
/// <see cref="UpdateNotes"/> 可在播放中更换剩余音符（实时移调）。
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    // —— 对外事件（工作线程触发，UI 需自行 marshal）——
    public event Action<string>? Log;
    public event Action<string>? CurrentNoteChanged;   // 正在吹奏的音符描述（"" 表示无）
    public event Action? Finished;                     // 自然播完或手动停止

    private sealed record PhysicalEvent(double T, int Kind, char Code, bool Down, string Label);

    private const int K_Key = 0;
    private const int K_MouseLeft = 1;
    private const int K_MouseRight = 2;
    private const int K_MouseMiddle = 3;

    private readonly object _gate = new();
    private List<PhysicalEvent> _events = new();
    private double _totalMusic;          // 音乐时间总长
    private double _speed = 1.0;
    private double _leadSec;             // 提前量（物理秒）
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

    private bool _breath;            // 呼吸休止开关

    /// <summary>开始播放。notes 为映射后 InRange 的音符（音乐时间，未乘速度）。</summary>
    public void Play(IReadOnlyList<MappedNote> notes, double speed, double leadMs, bool loop, bool breath = false)
    {
        lock (_gate)
        {
            if (_running) StopInternal();

            _speed = speed <= 0 ? 1.0 : Math.Clamp(speed, 0.1, 5.0);
            _leadSec = Math.Clamp(leadMs, 0, 500) / 1000.0;
            _loop = loop;
            _breath = breath;
            _manualStop = false;
            _loopCount = 0;
            _snapNote = "";
            _snapLoop = 0;

            (_events, _totalMusic) = BuildSchedule(notes, _breath);
            _snapTotal = _totalMusic;

            _musicNow = 0;
            _nextIdx = 0;

            Log?.Invoke($"开始播放：共 {notes.Count} 个音符，总时长 ≈ {_totalMusic:F1}s" +
                        (_loop ? "（循环）" : ""));

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
            InputSender.ReleaseEverything();
            SetCurrentNote("");

            var inRange = notes.Where(n => n.InRange).ToList();
            var (evs, total) = BuildSchedule(inRange, _breath);
            _events = evs;
            if (total > 0) _totalMusic = Math.Max(_totalMusic, total);
            _nextIdx = FindNextIdx(_musicNow);
            Log?.Invoke($"已应用移调：剩余可演奏 {inRange.Count} 音");
        }
    }

    /// <summary>跳转到总进度 0..1 的位置（播放中可用）。</summary>
    public void SeekFraction(double fraction)
    {
        lock (_gate)
        {
            if (!_running) return;
            InputSender.ReleaseEverything();
            SetCurrentNote("");
            _musicNow = Math.Clamp(fraction, 0, 1) * _totalMusic;
            _nextIdx = FindNextIdx(_musicNow);
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

                // 2) 派发到期的物理事件（提前量折算成音乐时间）
                double lead = _leadSec * _speed;
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
    /// 定位“下一个待派发”事件：保留当前点之前 60ms 的预滚窗口，
    /// 让跳转/换谱后紧接的音符能先重放它的八度/升半音修饰键。
    /// </summary>
    private int FindNextIdx(double musicNow)
    {
        double floor = musicNow - 0.06;
        int idx = 0;
        while (idx < _events.Count && _events[idx].T <= floor) idx++;
        return idx;
    }

    private void Execute(PhysicalEvent ev)
    {
        switch (ev.Kind)
        {
            case K_Key:
                if (ev.Down) InputSender.KeyDown(ev.Code);
                else InputSender.KeyUp(ev.Code);
                break;
            case K_MouseLeft:
                if (ev.Down) InputSender.MouseDown(InputSender.MouseButton.Left);
                else InputSender.MouseUp(InputSender.MouseButton.Left);
                break;
            case K_MouseRight:
                if (ev.Down) InputSender.MouseDown(InputSender.MouseButton.Right);
                else InputSender.MouseUp(InputSender.MouseButton.Right);
                break;
            case K_MouseMiddle:
                if (ev.Down) InputSender.MouseDown(InputSender.MouseButton.Middle);
                else InputSender.MouseUp(InputSender.MouseButton.Middle);
                break;
        }
    }

    private void SetCurrentNote(string label)
    {
        _snapNote = label;
        CurrentNoteChanged?.Invoke(label);
    }

    // ================= 事件表构建（音乐时间） =================

    /// <summary>
    /// 把一个主旋律音符序列压成物理键盘/鼠标事件时间表。
    /// 时间为“音乐时间”（秒，与速度无关）。单音旋律策略：
    /// 同键重叠时短暂抬起重触发；跨键重叠时切断前音。
    /// breath=true 时启用“呼吸休止”：连续吹太久会自动插入极短停顿，避免游戏判定粘连。
    /// </summary>
    private static (List<PhysicalEvent>, double) BuildSchedule(IReadOnlyList<MappedNote> notes, bool breath)
    {
        var evs = new List<PhysicalEvent>();
        if (notes.Count == 0) return (evs, 0);

        var ordered = notes
            .OrderBy(n => n.Start)
            .ThenBy(n => n.End)
            .ToList();

        // —— 呼吸休止：把“连续发声 > 阈值”后的音符整体后移一小段，形成自然换气 ——
        if (breath)
        {
            const double breathGap = 0.09;     // 一次换气 ≈ 90ms
            const double phraseGap = 0.18;     // 空隙超过它视为已换气
            const double maxWind = 8.0;        // 连续吹满 8 秒触发一次
            double delay = 0, prevEnd = -1, wind = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                var n = ordered[i];
                double gap = prevEnd < 0 ? double.MaxValue : n.Start - prevEnd;
                double start = n.Start + delay;
                double end = n.End + delay;
                wind = gap < phraseGap ? wind + (end - start) : (end - start);
                if (wind > maxWind)
                {
                    delay += breathGap;
                    start = n.Start + delay;
                    end = n.End + delay;
                    wind = end - start;   // 重置风量
                }
                ordered[i] = new MappedNote
                {
                    Pitch = n.Pitch, Start = start, End = end,
                    Key = n.Key, Sharp = n.Sharp,
                    OctaveSlot = n.OctaveSlot, InRange = n.InRange,
                    SkipReason = n.SkipReason
                };
                prevEnd = end;
            }
        }

        char? heldKey = null;
        double heldDownT = 0;
        double heldUpT = 0;
        var lastRelease = new Dictionary<char, double>();

        Slot heldSlot = Slot.Mid;
        bool heldSharp = false;

        foreach (var n in ordered)
        {
            double t = Math.Max(0, n.Start);
            double tEnd = Math.Max(t + 0.02, n.End);

            if (heldKey is char prev)
            {
                double upT = heldUpT;
                if (upT > t) upT = t - 0.02;
                if (upT < heldDownT + 0.01) upT = heldDownT + 0.01;
                evs.Add(new PhysicalEvent(upT, K_Key, prev, false, ""));
                lastRelease[prev] = upT;
                heldKey = null;
            }

            double downT = t;
            if (lastRelease.TryGetValue(n.Key, out double lu) && downT < lu + 0.012)
                downT = lu + 0.012;

            if (n.OctaveSlot != heldSlot)
            {
                if (heldSlot == Slot.Low)
                    evs.Add(new PhysicalEvent(downT - 0.016, K_MouseLeft, ' ', false, ""));
                else if (heldSlot == Slot.High)
                    evs.Add(new PhysicalEvent(downT - 0.016, K_MouseRight, ' ', false, ""));

                if (n.OctaveSlot == Slot.Low)
                    evs.Add(new PhysicalEvent(downT - 0.012, K_MouseLeft, ' ', true, ""));
                else if (n.OctaveSlot == Slot.High)
                    evs.Add(new PhysicalEvent(downT - 0.012, K_MouseRight, ' ', true, ""));

                heldSlot = n.OctaveSlot;
            }

            if (n.Sharp != heldSharp)
            {
                evs.Add(new PhysicalEvent(downT - 0.008, K_MouseMiddle, ' ', n.Sharp, ""));
                heldSharp = n.Sharp;
            }

            evs.Add(new PhysicalEvent(downT, K_Key, n.Key, true,
                NoteMapper.Describe(n, withTime: false)));

            heldKey = n.Key;
            heldDownT = downT;
            heldUpT = tEnd;
        }

        if (heldKey is char last)
        {
            double upT = Math.Max(heldUpT, heldDownT + 0.01);
            evs.Add(new PhysicalEvent(upT, K_Key, last, false, ""));
        }

        for (int i = 0; i < evs.Count; i++)
        {
            if (evs[i].T < 0) evs[i] = evs[i] with { T = 0 };
        }
        evs = evs.OrderBy(e => e.T).ToList();
        double total = evs.Count == 0 ? 0 : evs[^1].T;
        return (evs, total);
    }
}
