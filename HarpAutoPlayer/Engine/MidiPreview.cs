using System.Runtime.InteropServices;

namespace HarpAutoPlayer.Engine;

/// <summary>
/// 内置试听：借用 Windows 自带的 MIDI 合成器（winmm 的 MIDI Out，默认设备通常是
/// "Microsoft GS Wavetable Synth"）发声，方便改谱时边改边听。
///
/// 为什么不用第三方音频库：本项目是自包含单文件发布 + 裁剪，引入 NAudio 之类会明显
/// 增加体积，而 winmm 是系统自带的，P/Invoke 即可 —— 与项目已有的 SendInput /
/// user32 调用风格一致，零新增依赖。
///
/// 打不开设备时 <see cref="IsAvailable"/> 为 false，界面据此把开关灰掉并说明原因。
/// </summary>
public sealed class MidiPreview : IDisposable
{
    private const uint MidiMapper = 0xFFFFFFFF;   // 选默认设备
    private const uint NoteOnBase = 0x90;         // 通道 0
    private const uint NoteOffBase = 0x80;
    private const uint ProgramChangeBase = 0xC0;
    private const int ProgramPiano = 0;           // 0 = Acoustic Grand Piano

    private readonly object _gate = new();
    private IntPtr _handle = IntPtr.Zero;
    private readonly HashSet<int> _sounding = new();
    private readonly List<System.Threading.Timer> _timers = new();
    private bool _disposed;

    /// <summary>设备是否可用。false 时所有方法都安全地什么都不做。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>不可用时的原因（写进日志用）。</summary>
    public string LastError { get; private set; } = "";

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutOpen(out IntPtr lphmo, uint uDeviceID,
        IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutClose(IntPtr hmo);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutShortMsg(IntPtr hmo, uint dwMsg);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiOutReset(IntPtr hmo);

    public MidiPreview()
    {
        try
        {
            uint rc = midiOutOpen(out IntPtr h, MidiMapper, IntPtr.Zero, IntPtr.Zero, 0);
            if (rc != 0)
            {
                LastError = $"midiOutOpen 失败（MMRESULT={rc}）";
                return;
            }
            _handle = h;
            IsAvailable = true;
            // 选钢琴音色：试听旋律比默认音色清楚
            Send(ProgramChangeBase | (uint)ProgramPiano);
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>
    /// 试听一个音。<paramref name="autoRelease"/> 为 true 时到点自动放开（适合点一下听一声）；
    /// 为 false 时一直响，直到显式调用 <see cref="StopNote"/>（适合整曲试听）。
    /// </summary>
    public void PlayNote(int pitch, int milliseconds = 420, int velocity = 96, bool autoRelease = true)
    {
        if (!IsAvailable || _disposed) return;
        int p = Math.Clamp(pitch, 0, 127);
        int v = Math.Clamp(velocity, 1, 127);
        lock (_gate)
        {
            NoteOff(p);
            Send(NoteOnBase | ((uint)p << 8) | ((uint)v << 16));
            _sounding.Add(p);
        }
        if (!autoRelease) return;

        // 自动放开。用一次性定时器而不是 Sleep，避免阻塞 UI 线程。
        System.Threading.Timer? t = null;
        t = new System.Threading.Timer(_ =>
        {
            lock (_gate) { NoteOff(p); }
            lock (_gate) { _timers.Remove(t!); }
            t!.Dispose();
        }, null, Math.Max(60, milliseconds), Timeout.Infinite);
        lock (_gate) { _timers.Add(t); }
    }

    /// <summary>放开某个音高（整曲试听时由界面按谱面时间调用）。</summary>
    public void StopNote(int pitch)
    {
        if (!IsAvailable || _disposed) return;
        lock (_gate) { NoteOff(Math.Clamp(pitch, 0, 127)); }
    }

    /// <summary>立刻放开所有正在响的音（停止播放 / 关窗口 / 切换开关时调用）。</summary>
    public void StopAll()
    {
        if (!IsAvailable || _disposed) return;
        lock (_gate)
        {
            foreach (int p in _sounding) NoteOff(p);
            _sounding.Clear();
        }
    }

    private void NoteOff(int pitch)
    {
        if (_handle == IntPtr.Zero) return;
        Send(NoteOffBase | ((uint)pitch << 8));
        _sounding.Remove(pitch);
    }

    private void Send(uint message)
    {
        if (_handle == IntPtr.Zero) return;
        try { midiOutShortMsg(_handle, message); } catch { /* 设备被拔掉等，忽略 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var t in _timers) { try { t.Dispose(); } catch { } }
            _timers.Clear();
            _sounding.Clear();
            if (_handle != IntPtr.Zero)
            {
                try { midiOutReset(_handle); } catch { }
                try { midiOutClose(_handle); } catch { }
                _handle = IntPtr.Zero;
            }
        }
        IsAvailable = false;
    }
}
