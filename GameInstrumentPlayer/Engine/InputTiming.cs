namespace GameInstrumentPlayer.Engine;

using GameInstrumentPlayer.Profiles;

/// <summary>
/// 输入时序预算（全部为**物理毫秒**，与播放速度无关）。
/// 游戏按键按帧采样（30fps=33ms/帧），修饰键与琴键间隔小于一帧会被折进同一帧而漏音，
/// 因此这些间隔必须按物理帧给足，不能被速度除小。
/// 数值来自当前乐器方案，也可以由「输入兼容」下拉框整体套用一档预设。
/// </summary>
public sealed record InputTiming
{
    /// <summary>游戏采样帧长估计（16.7 = 60fps；33.3 = 30fps）。</summary>
    public double FrameMs { get; init; } = 16.7;

    /// <summary>修饰键必须比琴键早这么多，才能被正确采样到。</summary>
    public double ModLeadMs { get; init; } = 40.0;      // ≈ 2.5 帧 @60fps

    /// <summary>同一把琴键两次按下的最小间隔（要跨过"抬起"帧，否则两音粘连）。</summary>
    public double RetriggerMs { get; init; } = 45.0;    // ≈ 3 帧 @60fps

    /// <summary>琴键最短按住时长（避免按下与抬起被折进同一帧）。</summary>
    public double MinHoldMs { get; init; } = 45.0;      // ≈ 3 帧 @60fps

    /// <summary>前音抬起 → 后音按下之间的最小间隔（跨帧安全边距）。</summary>
    public double ReleaseGapMs { get; init; } = 40.0;   // ≈ 2.5 帧 @60fps

    /// <summary>提前派发的物理时间（固定值，不再乘速度）。须 ≥ ModLeadMs + FrameMs。</summary>
    public double LeadMs { get; init; } = 57.0;         // ≥ 40 + 16.7

    /// <summary>档位中文名（界面用）。</summary>
    public string Name { get; init; } = "方案默认";

    /// <summary>稳健档：给 30fps、掉帧或机器负载高时留余量。</summary>
    public static InputTiming Safe => new()
    {
        Name = "稳健",
        FrameMs = 33.3,
        ModLeadMs = 70,
        RetriggerMs = 80,
        MinHoldMs = 80,
        ReleaseGapMs = 70,
        LeadMs = 104         // ≥ 70 + 33.3
    };

    /// <summary>标准档：60fps 默认，绝大多数机器适用。</summary>
    public static InputTiming Standard => new();

    /// <summary>极限档：帧率很高、要跟极快的歌时用；时间余量最小，漏音风险自负。</summary>
    public static InputTiming Aggressive => new()
    {
        Name = "极限",
        FrameMs = 8.0,
        ModLeadMs = 20,
        RetriggerMs = 22,
        MinHoldMs = 22,
        ReleaseGapMs = 18,
        LeadMs = 28          // ≥ 20 + 8
    };

    /// <summary>只取方案里写的时序数值，名字保持「方案默认」。</summary>
    public static InputTiming FromProfile(InstrumentProfile profile) => new()
    {
        FrameMs = Math.Max(1, profile.FrameMs),
        ModLeadMs = Math.Max(0, profile.ModLeadMs),
        RetriggerMs = Math.Max(0, profile.RetriggerMs),
        MinHoldMs = Math.Max(0, profile.HoldMs),
        ReleaseGapMs = Math.Max(0, profile.ReleaseGapMs),
        LeadMs = Math.Max(0, profile.ModLeadMs) + Math.Max(1, profile.FrameMs)
    };

    /// <summary>把档位的余量倍数套到方案数值上：0.5 = 方案的一半，2 = 方案的两位（稳健）。</summary>
    public static InputTiming Scale(InstrumentProfile profile, double factor, string name)
    {
        var b = FromProfile(profile);
        double f = Math.Max(0.25, factor);
        return new InputTiming
        {
            Name = name,
            FrameMs = b.FrameMs * f,
            ModLeadMs = b.ModLeadMs * f,
            RetriggerMs = b.RetriggerMs * f,
            MinHoldMs = b.MinHoldMs * f,
            ReleaseGapMs = b.ReleaseGapMs * f,
            LeadMs = (b.ModLeadMs + b.FrameMs) * f
        };
    }

    /// <summary>按界面下拉框序号取档位（0 稳健 / 1 方案默认 / 2 极限）。</summary>
    public static InputTiming FromIndex(int index) => index switch
    {
        0 => Safe,
        2 => Aggressive,
        _ => Standard
    };

    /// <summary>按界面下拉框序号取档位，基准是当前方案。</summary>
    public static InputTiming FromIndex(int index, InstrumentProfile profile) => index switch
    {
        0 => Scale(profile, 2.0, "稳健"),
        2 => Scale(profile, 0.5, "极限"),
        _ => FromProfile(profile)
    };

    public static string[] Names => new[] { "稳健（掉帧留余量）", "方案默认（推荐）", "极限（高帧率）" };
}

/// <summary>
/// 输入时序诊断：统计"音键按下时修饰键已稳定多久"，用于在真机验证是否还有漏音；
/// 修饰键提前量不足一帧、同刻、同键重触发过密即为漏音的现场证据。
/// </summary>
public sealed class InputTimingProbe
{
    private readonly object _gate = new();

    private double _lastModMusicT = double.NegativeInfinity;
    private double _lastKeyDownMusicT = double.NegativeInfinity;
    private string _lastKey = "";
    private int _heldMods;

    public int TotalNoteOn { get; private set; }
    public int ModLeadTooShort { get; private set; }    // 修饰键提前量不足一帧
    public int ModLeadZero { get; private set; }        // 修饰键与音键同刻
    public int RetriggerTooShort { get; private set; }  // 同键重触发间隔不足
    public int MinHoldTooShort { get; private set; }    // 音键按住时长不足一帧
    public double MinModLeadMs { get; private set; } = double.MaxValue;

    /// <summary>修饰键状态变化（按音乐时间记录）。</summary>
    public void OnModifier(double musicT, bool down)
    {
        lock (_gate)
        {
            _lastModMusicT = musicT;
            _heldMods += down ? 1 : -1;
            if (_heldMods < 0) _heldMods = 0;
        }
    }

    /// <summary>琴键按下（按音乐时间记录）。key 是动作写法，如 key:Z / mouse:left。</summary>
    public void OnNoteOn(string key, double musicT)
    {
        lock (_gate)
        {
            TotalNoteOn++;
            if (_lastKey == key)
            {
                double gapMs = (musicT - _lastKeyDownMusicT) * 1000.0;
                if (gapMs < 45.0) RetriggerTooShort++;
            }
            if (!double.IsNegativeInfinity(_lastModMusicT))
            {
                double leadMs = (musicT - _lastModMusicT) * 1000.0;
                if (leadMs < MinModLeadMs) MinModLeadMs = leadMs;
                if (leadMs < 16.7) ModLeadTooShort++;
                if (leadMs < 0.5) ModLeadZero++;
            }
            _lastKey = key;
            _lastKeyDownMusicT = musicT;
        }
    }

    /// <summary>琴键抬起，用于统计按住时长。</summary>
    public void OnNoteOff(string key, double musicT)
    {
        lock (_gate)
        {
            if (_lastKey == key)
            {
                double holdMs = (musicT - _lastKeyDownMusicT) * 1000.0;
                if (holdMs < 16.7) MinHoldTooShort++;
            }
        }
    }

    /// <summary>跳转/换谱后重置（此时会强制释放全部修饰键）。</summary>
    public void Reset(double musicT)
    {
        lock (_gate)
        {
            _lastModMusicT = double.NegativeInfinity;
            _heldMods = 0;
            _lastKey = "";
            _lastKeyDownMusicT = double.NegativeInfinity;
        }
    }

    public string Summary()
    {
        lock (_gate)
        {
            if (TotalNoteOn == 0) return "时序诊断：无音符";
            double minLead = MinModLeadMs == double.MaxValue ? 0 : MinModLeadMs;
            return $"时序诊断：音符 {TotalNoteOn} 个；" +
                   $"修饰键提前量<16.7ms 的 {ModLeadTooShort} 个（同刻 {ModLeadZero} 个）；" +
                   $"同键重触发<45ms 的 {RetriggerTooShort} 个；" +
                   $"按住<16.7ms 的 {MinHoldTooShort} 个；" +
                   $"最小修饰键提前量 {minLead:F1}ms";
        }
    }
}
