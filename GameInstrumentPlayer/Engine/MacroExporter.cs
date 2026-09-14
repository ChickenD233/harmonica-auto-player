using System.Globalization;
using System.Text;
using GameInstrumentPlayer.Profiles;

namespace GameInstrumentPlayer.Engine;

/// <summary>
/// 把演奏事件表导出成外部工具能直接用的脚本，这样玩家不需要本程序常驻也能演奏。
/// 与「实际演奏」共用同一套调度（<see cref="PlaybackEngine.BuildSchedulePreview"/>），导出结果与播放时发出的按键完全一致。
/// 支持 AutoHotkey（通用、可改）、罗技 G HUB 的 Lua、以及通用 CSV（时刻/动作/按键）。
/// </summary>
public static class MacroExporter
{
    /// <summary>导出格式。</summary>
    public enum Format { LogitechGHub, KeystrokeCsv, Autohotkey }

    public static string Extension(Format f) => f switch
    {
        Format.LogitechGHub => ".lua",
        Format.Autohotkey => ".ahk",
        _ => ".csv"
    };

    public static string DisplayName(Format f) => f switch
    {
        Format.LogitechGHub => "罗技 G HUB 脚本 (.lua)",
        Format.Autohotkey => "AutoHotkey 脚本 (.ahk)",
        _ => "通用 CSV (.csv)"
    };

    /// <summary>
    /// 由映射后的音符生成脚本。notes 可含超音域音（会自动跳过）；
    /// speed 用于把音乐时间换算成实际播放时刻；timing 决定修饰键提前量，需与实际演奏一致。
    /// </summary>
    public static string Build(IReadOnlyList<MappedNote> notes, Format format,
                               double speed = 1.0, InputTiming? timing = null,
                               string songName = "", InstrumentProfile? profile = null)
    {
        var events = PlaybackEngine.BuildSchedulePreview(notes, timing, speed);
        string mode = (profile?.OutputMethod == OutputMethods.KeyboardVirtualKey) ? "vk" : "scan";
        return format switch
        {
            Format.LogitechGHub => BuildLua(events, songName, speed),
            Format.Autohotkey => BuildAhk(events, songName, speed, mode),
            _ => BuildCsv(events, songName)
        };
    }

    // ---------------------------------------------------------------- 公共：按键名映射

    /// <summary>把内部动作写成 AutoHotkey 的按键名。</summary>
    private static string AhkKeyName(string target)
    {
        if (target.StartsWith("mouse:", StringComparison.Ordinal))
        {
            return target[6..].ToLowerInvariant() switch
            {
                "left" => "LButton",
                "middle" => "MButton",
                "right" => "RButton",
                "x1" => "XButton1",
                _ => "XButton2"
            };
        }

        string name = target.StartsWith("key:", StringComparison.Ordinal) ? target[4..] : target;
        if (name.Length == 1) return name.ToLowerInvariant();
        return name.ToLowerInvariant() switch
        {
            "leftshift" => "LShift",
            "rightshift" => "RShift",
            "leftctrl" => "LCtrl",
            "rightctrl" => "RCtrl",
            "leftalt" => "LAlt",
            "rightalt" => "RAlt",
            "space" => "Space",
            "tab" => "Tab",
            "enter" => "Enter",
            "capslock" => "CapsLock",
            _ => name.ToLowerInvariant()
        };
    }

    /// <summary>把内部动作写成 G HUB Lua 的按键名。</summary>
    private static string LuaKeyName(string target)
    {
        if (target.StartsWith("mouse:", StringComparison.Ordinal)) return "mouse";
        string name = target.StartsWith("key:", StringComparison.Ordinal) ? target[4..] : target;
        return name.ToLowerInvariant() switch
        {
            "leftshift" => "lshift",
            "rightshift" => "rshift",
            "leftctrl" => "lctrl",
            "rightctrl" => "rctrl",
            "leftalt" => "lalt",
            "rightalt" => "ralt",
            "space" => "space",
            "tab" => "tab",
            "enter" => "enter",
            "capslock" => "capslock",
            _ => name.ToLowerInvariant()
        };
    }

    private static int MouseButtonNo(string target)
    {
        if (!target.StartsWith("mouse:", StringComparison.Ordinal)) return 0;
        return target[6..].ToLowerInvariant() switch
        {
            "left" => 1,
            "middle" => 3,
            "right" => 2,
            "x1" => 4,
            _ => 5
        };
    }

    private static string MouseName(string target)
        => MouseButtonNo(target) switch
        {
            1 => "左键",
            2 => "右键",
            3 => "中键",
            4 => "侧键1",
            5 => "侧键2",
            _ => "鼠标键"
        };

    // ---------------------------------------------------------------- AutoHotkey

    /// <summary>
    /// AutoHotkey v1 脚本。用 SendEvent 逐条发送，时刻表由脚本自己 sleep 推进。
    /// 装完 AutoHotkey 后双击即可运行；脚本里的 SendMode 与方案里的输出方式一致。
    /// </summary>
    private static string BuildAhk(
        IReadOnlyList<PlaybackEngine.ScheduledEvent> events, string songName, double speed, string mode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; ============================================================");
        sb.AppendLine("; MIDI 转游戏乐器 · 按键脚本（AutoHotkey v1）");
        sb.AppendLine("; 用法：装好 AutoHotkey 后双击本文件；按 F8 开始演奏，按 F9 退出。");
        sb.AppendLine(";       开始前先切到游戏窗口，并让对方处于可以演奏的状态。");
        if (!string.IsNullOrWhiteSpace(songName)) sb.AppendLine($"; 曲目：{songName}");
        sb.AppendLine($"; 速度：{speed * 100:F0}%（时刻表已按此速度换算）");
        sb.AppendLine($"; 事件数：{events.Count}");
        sb.AppendLine("; ============================================================");
        sb.AppendLine("#NoEnv");
        sb.AppendLine("#SingleInstance force");
        sb.AppendLine("SendMode Event");
        sb.AppendLine("SetKeyDelay, 0, 0");
        sb.AppendLine("SetBatchLines, -1");
        sb.AppendLine("CoordMode, Mouse, Screen");
        if (mode == "vk")
            sb.AppendLine("; 本方案使用虚拟键码输出；AHK 的 Send 默认就是虚拟键码，无需改动。");
        else
            sb.AppendLine("; 本方案使用键盘扫描码输出。AHK 的 Send 默认按虚拟键码发送；");
        sb.AppendLine("; 若游戏收不到按键，把下面的 Send 换成 SendInput 或改用其它导出格式。");
        sb.AppendLine();
        sb.AppendLine("F8::");
        sb.AppendLine("  Sleep, 800");
        sb.AppendLine("  start := A_TickCount");
        sb.AppendLine();
        sb.AppendLine("  ; ---- 事件表：先等待，再动作 ----");

        double lastMs = 0;
        foreach (var e in events)
        {
            double ms = e.MusicTime * 1000.0;
            double wait = ms - lastMs;
            if (wait < 0) wait = 0;
            lastMs = ms;
            bool mouse = e.Target.StartsWith("mouse:", StringComparison.Ordinal);
            string who = AhkKeyName(e.Target);

            sb.AppendLine($"  Sleep, {Math.Round(wait).ToString(CultureInfo.InvariantCulture)} ; {ms:F1}ms");
            if (mouse)
                sb.AppendLine($"  {(e.Down ? "MouseDown" : "MouseUp")}, {who}");
            else if (e.Down)
                sb.AppendLine($"  Send, {{{who} down}}");
            else
                sb.AppendLine($"  Send, {{{who} up}}");
        }

        sb.AppendLine("  Send, {LShift up}{RShift up}{LCtrl up}{RCtrl up}{LAlt up}{RAlt up}");
        sb.AppendLine("  ToolTip, 演奏结束");
        sb.AppendLine("  SetTimer, RemoveToolTip, -2000");
        sb.AppendLine("return");
        sb.AppendLine();
        sb.AppendLine("F9::ExitApp");
        sb.AppendLine("RemoveToolTip:");
        sb.AppendLine("  ToolTip");
        sb.AppendLine("return");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- G HUB Lua

    /// <summary>
    /// 罗技 G HUB 的 Lua 脚本。
    /// G HUB 官方脚本 API：PressKey / ReleaseKey / PressMouseButton / ReleaseMouseButton / Sleep。
    /// </summary>
    private static string BuildLua(IReadOnlyList<PlaybackEngine.ScheduledEvent> events,
                                   string songName, double speed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- MIDI 转游戏乐器 导出的按键脚本（罗技 G HUB）");
        sb.AppendLine("-- 用法：G HUB → 选中设备 → 「游戏与应用程序」→ 添加游戏 → 编写脚本 → 编辑脚本");
        sb.AppendLine("--       把本文件内容整段粘贴进去，保存；在游戏里按下你绑定的脚本触发键即可演奏。");
        sb.AppendLine("-- 注意：G HUB 的 Lua 环境不保证暴露鼠标侧键。若脚本里出现侧键而无效，");
        sb.AppendLine("--       请改用键盘键，或改用 CSV 形式交给其它工具。");
        if (!string.IsNullOrWhiteSpace(songName))
            sb.AppendLine($"-- 曲目：{songName}");
        sb.AppendLine($"-- 速度：{speed * 100:F0}%（导出的时刻已按此速度换算）");
        sb.AppendLine($"-- 事件数：{events.Count}");
        sb.AppendLine();
        sb.AppendLine("local events = {");

        double lastMs = 0;
        foreach (var e in events)
        {
            double ms = e.MusicTime * 1000.0;
            double wait = ms - lastMs;
            if (wait < 0) wait = 0;
            lastMs = ms;

            string action;
            if (e.Target.StartsWith("mouse:", StringComparison.Ordinal))
                action = e.Down ? $"PressMouseButton({MouseButtonNo(e.Target)})"
                                : $"ReleaseMouseButton({MouseButtonNo(e.Target)})";
            else
                action = e.Down ? $"PressKey(\"{LuaKeyName(e.Target)}\")"
                                : $"ReleaseKey(\"{LuaKeyName(e.Target)}\")";

            sb.AppendLine($"  {{{wait.ToString("F1", CultureInfo.InvariantCulture)}, function() {action} end}},");
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("-- 触发方式：在 G HUB 里把本脚本绑定到某个 G 键或鼠标键，");
        sb.AppendLine("-- 按下它就开始演奏（建议单独绑一个不常用的键，避免和游戏操作冲突）。");
        sb.AppendLine("function OnEvent(event, arg)");
        sb.AppendLine("  if event == \"G_PRESSED\" or event == \"MOUSE_BUTTON_PRESSED\" then");
        sb.AppendLine("    for i = 1, #events do");
        sb.AppendLine("      Sleep(events[i][1])");
        sb.AppendLine("      events[i][2]()");
        sb.AppendLine("    end");
        sb.AppendLine("  end");
        sb.AppendLine("end");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- CSV

    private static string BuildCsv(IReadOnlyList<PlaybackEngine.ScheduledEvent> events, string songName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MIDI 转游戏乐器 按键时刻表");
        sb.AppendLine($"# 曲目：{songName}");
        sb.AppendLine("# 列：相对毫秒, 动作, 目标, 类型");
        sb.AppendLine("time_ms,action,target,kind");
        foreach (var e in events)
        {
            bool mouse = e.Target.StartsWith("mouse:", StringComparison.Ordinal);
            string target = mouse ? MouseName(e.Target) : e.TargetLabel;
            string action = e.Down ? "down" : "up";
            sb.AppendLine($"{(e.MusicTime * 1000.0).ToString("F1", CultureInfo.InvariantCulture)}," +
                          $"{action},{target},{(mouse ? "mouse" : "key")}");
        }
        return sb.ToString();
    }

    /// <summary>导出时给出的事件摘要（用于界面提示）。</summary>
    public static (int Notes, int Events, double Seconds) Summarize(
        IReadOnlyList<MappedNote> notes, double speed = 1.0, InputTiming? timing = null)
    {
        var events = PlaybackEngine.BuildSchedulePreview(notes, timing, speed);
        var inRange = notes.Where(n => n.InRange).ToList();
        double end = events.Count == 0 ? 0 : events[^1].MusicTime;
        return (inRange.Count, events.Count, end);
    }
}
