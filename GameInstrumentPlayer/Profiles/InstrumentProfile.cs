using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using GameInstrumentPlayer.Midi;

namespace GameInstrumentPlayer.Profiles;

/// <summary>输出设备的种类：键盘按键，或鼠标键。</summary>
public enum ActionKind
{
    Key = 0,
    Mouse = 1
}

/// <summary>鼠标键编号。</summary>
public enum MouseButtonId
{
    Left = 0,
    Middle = 1,
    Right = 2,
    X1 = 3,
    X2 = 4
}

/// <summary>
/// 一次输出动作：键盘上的一个物理键，或一个鼠标键。
/// Code 的取值规则：<see cref="ActionKind.Key"/> 时 'A'..'Z'、'0'..'9'、','、'.'、';'、'/'
/// 依次对应键盘上的同名键；<see cref="ActionKind.Mouse"/> 时是 <see cref="MouseButtonId"/> 的数值加 1。
/// </summary>
public readonly record struct NoteAction(ActionKind Kind, int Code)
{
    /// <summary>空动作（无键可发）。</summary>
    public static NoteAction None => new(ActionKind.Key, 0);

    public bool IsNone => Code == 0;

    /// <summary>内部稳定写法，用于日志、导出与去重。</summary>
    public string Token => Kind switch
    {
        ActionKind.Mouse => $"mouse:{((MouseButtonId)(Code - 1)).ToString().ToLowerInvariant()}",
        _ => "key:" + ActionCodec.KeyNameOf(Code)
    };

    public override string ToString() => IsNone ? "—" : Token;

    /// <summary>界面用的短名：Z、LeftShift、鼠标左键。</summary>
    public string Label => Kind switch
    {
        ActionKind.Mouse => MouseButtonLabel(Code),
        _ => Code == 0 ? "—" : ActionCodec.KeyNameOf(Code)
    };

    private static string MouseButtonLabel(int code) => (MouseButtonId)(code - 1) switch
    {
        MouseButtonId.Left => "鼠标左键",
        MouseButtonId.Middle => "鼠标中键",
        MouseButtonId.Right => "鼠标右键",
        MouseButtonId.X1 => "鼠标侧键1",
        _ => "鼠标侧键2"
    };
}

/// <summary>
/// 把 NoteAction 写成配置里的文本，或从文本读回。
/// 文本写法：<c>key:Z</c>、<c>key:LEFTSHIFT</c>、<c>mouse:left</c>、<c>mouse:3</c>；省略 <c>key:</c> 也可以。
/// 键盘虚拟键码的换算也放在这里，供输出层复用。
/// </summary>
public static class ActionCodec
{
    public static string Format(NoteAction a) => a.IsNone ? "" : a.Token;

    /// <summary>键名 → 虚拟键码。0 表示不认识这个键名。</summary>
    public static ushort VirtualKeyOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        string s = name.Trim();
        if (s.Length == 1)
        {
            char c = char.ToUpperInvariant(s[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
            return c switch
            {
                ',' => 0xBC,   // VK_OEM_COMMA
                '.' => 0xBE,   // VK_OEM_PERIOD
                ';' => 0xBA,   // VK_OEM_1
                '/' => 0xBF,   // VK_OEM_2
                '-' => 0xBD,   // VK_OEM_MINUS
                '=' => 0xBB,   // VK_OEM_PLUS
                '[' => 0xDB,
                ']' => 0xDD,
                '\\' => 0xDC,
                '\'' => 0xDE,
                '`' => 0xC0,
                _ => 0
            };
        }

        return s.ToLowerInvariant() switch
        {
            "leftshift" or "shift" or "lshift" => 0xA0,
            "rightshift" or "rshift" => 0xA1,
            "leftctrl" or "leftcontrol" or "ctrl" or "lctrl" => 0xA2,
            "rightctrl" or "rctrl" => 0xA3,
            "leftalt" or "alt" or "lalt" => 0xA4,
            "rightalt" or "ralt" => 0xA5,
            "space" or "spacebar" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "capslock" => 0x14,
            _ => 0
        };
    }

    /// <summary>虚拟键码 → 键名（保存方案与导出脚本时用）。空串表示不认识。</summary>
    public static string NameOfVirtualKey(ushort vk)
    {
        if (vk is >= 'A' and <= 'Z') return ((char)vk).ToString();
        if (vk is >= '0' and <= '9') return ((char)vk).ToString();
        return vk switch
        {
            0xBC => ",", 0xBE => ".", 0xBA => ";", 0xBF => "/", 0xBD => "-",
            0xBB => "=", 0xDB => "[", 0xDD => "]", 0xDC => "\\", 0xDE => "'", 0xC0 => "`",
            0xA0 => "LeftShift", 0xA1 => "RightShift",
            0xA2 => "LeftCtrl", 0xA3 => "RightCtrl",
            0xA4 => "LeftAlt", 0xA5 => "RightAlt",
            0x20 => "Space", 0x09 => "Tab", 0x0D => "Enter", 0x14 => "CapsLock",
            _ => ""
        };
    }

    /// <summary>该动作是不是一个受支持的键盘键或鼠标键。</summary>
    public static bool CanSend(NoteAction a)
    {
        if (a.IsNone) return false;
        if (a.Kind == ActionKind.Mouse) return a.Code is >= 1 and <= 5;
        return VirtualKeyOf(((char)a.Code).ToString()) != 0
            || VirtualKeyOf(KeyNameOf(a.Code)) != 0;
    }

    /// <summary>键盘动作的键名：单字符键就是它本身，命名键（修饰键等）用全名。</summary>
    public static string KeyNameOf(int code)
    {
        if (code is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)code).ToString();
        return code >= 0x100 ? NamedKeyName(code) : "";
    }

    /// <summary>命名键的内部编号（放在 0x100 以上，避免和字符键冲突）。</summary>
    private static readonly (string Name, ushort Vk)[] NamedKeys =
    {
        ("LeftShift", 0xA0), ("RightShift", 0xA1),
        ("LeftCtrl", 0xA2), ("RightCtrl", 0xA3),
        ("LeftAlt", 0xA4), ("RightAlt", 0xA5),
        ("Space", 0x20), ("Tab", 0x09), ("Enter", 0x0D), ("CapsLock", 0x14)
    };

    /// <summary>命名键的内部编号；不认识返回 -1。0x101 起，避开字符键的取值。</summary>
    private static int NamedKeyCode(string name)
    {
        for (int i = 0; i < NamedKeys.Length; i++)
            if (string.Equals(NamedKeys[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return 0x101 + i;
        return -1;
    }

    private static string NamedKeyName(int code)
    {
        int i = code - 0x101;
        return i >= 0 && i < NamedKeys.Length ? NamedKeys[i].Name : "";
    }

    /// <summary>解析 "key:Z" / "key:LeftShift" / "mouse:left" / "Z" / "mouse:3"；无法解析返回空动作。</summary>
    public static NoteAction Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return NoteAction.None;
        string s = text.Trim();

        if (s.StartsWith("mouse:", StringComparison.OrdinalIgnoreCase))
        {
            string name = s[6..].Trim();
            MouseButtonId? id = name.ToLowerInvariant() switch
            {
                "left" or "l" => MouseButtonId.Left,
                "middle" or "m" => MouseButtonId.Middle,
                "right" or "r" => MouseButtonId.Right,
                "x1" or "4" => MouseButtonId.X1,
                "x2" or "5" => MouseButtonId.X2,
                _ => null
            };
            if (id == null && int.TryParse(name, out int n) && n is >= 1 and <= 5)
                id = (MouseButtonId)(n - 1);
            return id == null ? NoteAction.None : new NoteAction(ActionKind.Mouse, (int)id + 1);
        }

        if (s.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
            s = s[4..].Trim();

        if (s.Length == 1) return new NoteAction(ActionKind.Key, char.ToUpperInvariant(s[0]));

        int named = NamedKeyCode(s);
        return named < 0 ? NoteAction.None : new NoteAction(ActionKind.Key, named);
    }
}

/// <summary>一个八度档位 = 一行琴键。</summary>
public sealed class ProfileRow
{
    /// <summary>这一行相对基准八度的偏移。0 = 基准八度，1 = 高一个八度。</summary>
    public int OctaveOffset { get; set; }

    /// <summary>与 Keys 一一对应的琴键。</summary>
    public List<string> Keys { get; set; } = new();
}

/// <summary>一个乐器方案：琴键布局 + 输出时序。用户可以复制、改键、存成自己的方案。</summary>
public sealed class InstrumentProfile
{
    /// <summary>内部标识（保存文件名用它）。</summary>
    public string Id { get; set; } = "";

    /// <summary>界面显示名。</summary>
    public string Name { get; set; } = "新方案";

    /// <summary>说明：这个方案对应哪些游戏、怎么用。</summary>
    public string Description { get; set; } = "";

    /// <summary>每行几个音，默认 7（do..ti）。</summary>
    public int NotesPerRow { get; set; } = 7;

    /// <summary>与 Keys 一一对应的音级半音数，0 = do。默认自然大调。</summary>
    public List<int> NoteOffsets { get; set; } = new() { 0, 2, 4, 5, 7, 9, 11 };

    /// <summary>第一行第一个音（最低琴键）的 MIDI 音高。C4 = 60。</summary>
    public int BasePitch { get; set; } = 60;

    /// <summary>自动选择八度时，基准音高允许的偏移（八度数）。</summary>
    public int AutoOctaveRange { get; set; } = 1;

    public List<ProfileRow> Rows { get; set; } = new();

    /// <summary>升半音的处理方式。默认按修饰键。</summary>
    public SharpMode SharpMode { get; set; } = SharpMode.Modifier;

    /// <summary>按升半音时同时按住的键，可空。</summary>
    public string SharpModifier { get; set; } = "";

    /// <summary>升半音只能靠换到另一行的写法：目标行相对本行的偏移。0 = 不用。</summary>
    public int SharpRowOffset { get; set; }

    /// <summary>输出方式（目前支持键盘扫描码注入）。</summary>
    public string OutputMethod { get; set; } = OutputMethods.KeyboardScanCode;

    /// <summary>最少按住毫秒。</summary>
    public int HoldMs { get; set; } = 40;

    /// <summary>同一把琴键两次按下的最小间隔毫秒。</summary>
    public int RetriggerMs { get; set; } = 45;

    /// <summary>修饰键比琴键早按下的毫秒。</summary>
    public int ModLeadMs { get; set; } = 40;

    /// <summary>松开前一个键到按下后一个键的间隔毫秒。</summary>
    public int ReleaseGapMs { get; set; } = 40;

    /// <summary>游戏采样一帧的估计毫秒。</summary>
    public int FrameMs { get; set; } = 17;

    /// <summary>用户自建方案（内置方案不可直接改，改了会另存为副本）。</summary>
    public bool UserDefined { get; set; }

    // ---------------------------------------------------------------- 派生量

    [JsonIgnore] public int RowCount => Rows.Count;
    [JsonIgnore] public int KeyCount => Rows.Sum(r => r.Keys.Count);
    [JsonIgnore] public int LowestOctaveOffset => Rows.Count == 0 ? 0 : Rows.Min(r => r.OctaveOffset);
    [JsonIgnore] public int HighestOctaveOffset => Rows.Count == 0 ? 0 : Rows.Max(r => r.OctaveOffset);

    /// <summary>这个方案最低可演奏的 MIDI 音高。</summary>
    [JsonIgnore]
    public int MinPitch => Rows.Count == 0 ? 0 : BasePitch + LowestOctaveOffset * 12 + Math.Min(0, NoteOffsets.Min());

    /// <summary>这个方案最高可演奏的 MIDI 音高。</summary>
    [JsonIgnore]
    public int MaxPitch => Rows.Count == 0 ? 0 : BasePitch + HighestOctaveOffset * 12 + NoteOffsets.Max();

    /// <summary>音域文字，如 C4 ~ B6（3 行 / 21 键）。</summary>
    [JsonIgnore]
    public string RangeLabel => RowCount == 0
        ? "未配置"
        : $"{Music.NoteName(Math.Clamp(MinPitch, 0, 127))} ~ {Music.NoteName(Math.Clamp(MaxPitch, 0, 127))}" +
          $"（{RowCount} 行 / {KeyCount} 键）";

    /// <summary>这个方案能演奏的全部音高（用于卷帘上色与自动选八度）。</summary>
    public IReadOnlyList<int> PlayablePitches(int basePitch)
    {
        var list = new List<int>();
        foreach (var row in Rows)
        {
            int root = basePitch + row.OctaveOffset * 12;
            foreach (var k in row.Keys)
            {
                var a = ActionCodec.Parse(k);
                if (a.IsNone) continue;
                foreach (int off in NoteOffsets)
                {
                    int p = root + off;
                    if (p is >= 0 and <= 127) list.Add(p);
                }
            }
        }
        return list;
    }

    /// <summary>校验并修复明显不合法的字段；返回错误说明（空 = 通过）。</summary>
    public string Validate()
    {
        if (Rows.Count == 0) return "方案里一行琴键都没有。";
        if (NoteOffsets.Count == 0) return "音级表为空。";
        if (NotesPerRow <= 0) return "每行音数必须大于 0。";
        var seen = new HashSet<int>();
        foreach (var row in Rows)
        {
            if (row.Keys.Count == 0) return $"第 {row.OctaveOffset + 1} 行没有琴键。";
            if (row.Keys.Count > NoteOffsets.Count)
                return $"第 {row.OctaveOffset + 1} 行有 {row.Keys.Count} 个键，多于音级表 {NoteOffsets.Count} 个。";
            if (row.Keys.Any(k => ActionCodec.Parse(k).IsNone))
                return $"第 {row.OctaveOffset + 1} 行有空的琴键。";
            if (!seen.Add(row.OctaveOffset))
                return $"有两行都是第 {row.OctaveOffset + 1} 八度，会互相覆盖。";
        }
        if (SharpMode == SharpMode.Modifier && string.IsNullOrWhiteSpace(SharpModifier))
            return "升半音方式是「按修饰键」，但没指定修饰键。";
        return "";
    }

    /// <summary>深拷贝（编辑内置方案时先复制再改，保证可以还原）。</summary>
    public InstrumentProfile Clone()
    {
        var copy = new InstrumentProfile
        {
            Id = Id, Name = Name, Description = Description,
            NotesPerRow = NotesPerRow, NoteOffsets = new List<int>(NoteOffsets),
            BasePitch = BasePitch, AutoOctaveRange = AutoOctaveRange,
            SharpMode = SharpMode, SharpModifier = SharpModifier, SharpRowOffset = SharpRowOffset,
            OutputMethod = OutputMethod, HoldMs = HoldMs, RetriggerMs = RetriggerMs,
            ModLeadMs = ModLeadMs, ReleaseGapMs = ReleaseGapMs, FrameMs = FrameMs,
            UserDefined = UserDefined,
            Rows = Rows.Select(r => new ProfileRow
            {
                OctaveOffset = r.OctaveOffset,
                Keys = new List<string>(r.Keys)
            }).ToList()
        };
        return copy;
    }

    /// <summary>按键位内容算稳定标识，用于判断两个方案是不是同一套布局。</summary>
    public string LayoutFingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(BasePitch).Append('|').Append(NotesPerRow).Append('|');
        sb.Append(string.Join(",", NoteOffsets)).Append('|');
        foreach (var r in Rows.OrderBy(r => r.OctaveOffset))
            sb.Append(r.OctaveOffset).Append(':').Append(string.Join(",", r.Keys)).Append(';');
        sb.Append('|').Append(SharpMode).Append('|').Append(SharpModifier).Append('|').Append(SharpRowOffset);
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..12];
    }
}

/// <summary>升半音的处理方式。</summary>
public enum SharpMode
{
    /// <summary>同时按一个修饰键（很多游戏的写法）。</summary>
    Modifier = 0,

    /// <summary>换到另一个八度行去找这个音。7 键一行、半音齐全的乐器常用。</summary>
    RowShift = 1,

    /// <summary>乐器没有半音：降到最近的音级。</summary>
    Snap = 2,

    /// <summary>乐器没有半音：直接跳过这个音。</summary>
    Skip = 3
}

/// <summary>升半音方式的界面文字。</summary>
public static class SharpModes
{
    /// <summary>没有半音：自动降级到最近的音级。</summary>
    public const SharpMode None = SharpMode.Snap;

    public static string Label(SharpMode m) => m switch
    {
        SharpMode.Modifier => "同时按修饰键",
        SharpMode.RowShift => "换到相邻八度行",
        SharpMode.Skip => "跳过这个音",
        _ => "自动降到最近的音"
    };

    public static string[] Labels => new[]
    {
        Label(SharpMode.Modifier), Label(SharpMode.RowShift),
        Label(SharpMode.Snap), Label(SharpMode.Skip)
    };

    public static SharpMode FromIndex(int i) => i switch
    {
        1 => SharpMode.RowShift,
        2 => SharpMode.Snap,
        3 => SharpMode.Skip,
        _ => SharpMode.Modifier
    };

    public static int IndexOf(SharpMode m) => m switch
    {
        SharpMode.RowShift => 1,
        SharpMode.Snap => 2,
        SharpMode.Skip => 3,
        _ => 0
    };
}

/// <summary>输出方式的名字。</summary>
public static class OutputMethods
{
    /// <summary>用 Windows SendInput 发键盘扫描码（多数游戏认这个）。</summary>
    public const string KeyboardScanCode = "keyboard-scancode";

    /// <summary>用 Windows SendInput 发虚拟键码（少数游戏只认这个）。</summary>
    public const string KeyboardVirtualKey = "keyboard-vk";

    public static string Label(string method) => method switch
    {
        KeyboardVirtualKey => "键盘虚拟键码（少数游戏）",
        _ => "键盘扫描码（推荐）"
    };

    public static string[] All => new[] { KeyboardScanCode, KeyboardVirtualKey };
}
