using System.Globalization;
using System.Text;
using HarpAutoPlayer.Midi;

namespace HarpAutoPlayer.Engine;

/// <summary>
/// 把演奏事件表导出成外部工具能用的按键脚本。
///
/// 导出内容与「实际演奏」走同一套调度（<see cref="PlaybackEngine.BuildSchedulePreview"/>），
/// 因此导出结果与播放时发出的按键完全一致，包括修饰键（鼠标左/右/中键）的按下与抬起。
///
/// 支持三种格式：
///   · LogitechGHub  —— 罗技 G HUB 的 Lua 脚本（G HUB → 游戏与应用程序 → 编写脚本 → 编辑 → 粘贴保存）
///   · KeystrokeCsv  —— 通用 CSV（时刻/动作/按键），可用于任何支持导入按键时序的工具
///
/// 注意：雷蛇 Synapse 的宏文件是私有格式、没有官方规范，硬编一个格式很容易导入失败，
/// 因此不提供"一键导入"文件；雷蛇用户请用 CSV/文本，或直接用宏录制功能录一遍。
/// </summary>
public static class MacroExporter
{
    /// <summary>导出格式。</summary>
    public enum Format { LogitechGHub, KeystrokeCsv }

    public static string Extension(Format f) => f switch
    {
        Format.LogitechGHub => ".lua",
        _ => ".csv"
    };

    /// <summary>
    /// 由映射后的音符生成按键脚本。
    /// </summary>
    /// <param name="notes">映射后的音符（可含超音域音，会自动跳过）。</param>
    /// <param name="format">导出格式。</param>
    /// <param name="speed">把音乐时间换算成实际播放时刻的倍率（与界面上的速度一致）。</param>
    /// <param name="timing">时序预算（决定修饰键提前量等，保持一致即可）。</param>
    /// <param name="songName">写进脚本注释的曲名。</param>
    public static string Build(IReadOnlyList<MappedNote> notes, Format format,
                               double speed = 1.0, InputTiming? timing = null,
                               string songName = "")
    {
        var events = PlaybackEngine.BuildSchedulePreview(notes, timing, speed);
        return format == Format.LogitechGHub
            ? BuildLua(events, songName, speed)
            : BuildCsv(events);
    }

    // ---------------------------------------------------------------- 公共：按键名映射

    /// <summary>把内部按键字符转成 G HUB Lua 的按键名。</summary>
    private static string LuaKeyName(char c) => c switch
    {
        'Z' => "z", 'X' => "x", 'C' => "c", 'V' => "v",
        'B' => "b", 'N' => "n", 'M' => "m",
        ',' => "comma",
        _ => c.ToString().ToLowerInvariant()
    };

    private static string MouseName(string kind) => kind switch
    {
        "mouse-left" => "左键",
        "mouse-right" => "右键",
        _ => "中键"
    };

    // ---------------------------------------------------------------- G HUB Lua

    /// <summary>
    /// 罗技 G HUB 的 Lua 脚本。
    /// G HUB 官方脚本 API：PressKey / ReleaseKey / PressMouseButton / ReleaseMouseButton / Sleep。
    /// 按键名用字符串（"z"…"comma"）；鼠标键用 1=左、2=右、3=中。
    /// </summary>
    private static string BuildLua(IReadOnlyList<PlaybackEngine.ScheduledEvent> events,
                                   string songName, double speed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- 口琴自动演奏器 导出的按键脚本（罗技 G HUB）");
        sb.AppendLine("-- 用法：G HUB → 选中设备 → 「游戏与应用程序」→ 添加游戏 → 编写脚本 → 编辑脚本");
        sb.AppendLine("--       把本文件内容整段粘贴进去，保存；在游戏里按下你绑定的脚本触发键即可播放。");
        sb.AppendLine("-- 注意：G HUB 的 Lua 环境不保证暴露鼠标中键（3）。若脚本里出现鼠标中键而无效，");
        sb.AppendLine("--       请把「升半音」改用键盘键，或改用 CSV 形式交给其它工具。");
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

            string action = e.Kind == "key"
                ? (e.Down ? $"PressKey(\"{LuaKeyName(e.Key)}\")" : $"ReleaseKey(\"{LuaKeyName(e.Key)}\")")
                : (e.Down ? $"PressMouseButton({MouseButtonNo(e.Kind)})"
                          : $"ReleaseMouseButton({MouseButtonNo(e.Kind)})");

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

    private static int MouseButtonNo(string kind) => kind switch
    {
        "mouse-left" => 1,
        "mouse-right" => 2,
        _ => 3
    };

    // ---------------------------------------------------------------- CSV

    private static string BuildCsv(IReadOnlyList<PlaybackEngine.ScheduledEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("time_ms,action,target,note");
        foreach (var e in events)
        {
            string target = e.Kind == "key" ? e.KeyLabel : MouseName(e.Kind);
            string action = e.Down ? "down" : "up";
            sb.AppendLine($"{(e.MusicTime * 1000.0).ToString("F1", CultureInfo.InvariantCulture)}," +
                          $"{action},{target},");
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
