using GameInstrumentPlayer.Midi;

namespace GameInstrumentPlayer.Profiles;

/// <summary>
/// 内置乐器方案。
///
/// 多数游戏的演奏模式都是「把主键盘区当成琴键」：一行 7 个音，多行就是多个八度。
/// 差别只有三处：几行、每行的键位、半音怎么按。用户可以在界面上复制、改键、另存。
/// 每行的音级表固定在 <see cref="InstrumentProfile.NoteOffsets"/>，默认是自然大调（do..ti）。
/// </summary>
public static class BuiltInProfiles
{
    /// <summary>自然大调音级：do re mi fa sol la ti。</summary>
    public static readonly int[] Major = { 0, 2, 4, 5, 7, 9, 11 };

    /// <summary>zxcvbnm。</summary>
    private static readonly string[] LowerRow = { "key:Z", "key:X", "key:C", "key:V", "key:B", "key:N", "key:M" };

    /// <summary>asdfghj。</summary>
    private static readonly string[] HomeRow = { "key:A", "key:S", "key:D", "key:F", "key:G", "key:H", "key:J" };

    /// <summary>qwertyu。</summary>
    private static readonly string[] UpperRow = { "key:Q", "key:W", "key:E", "key:R", "key:T", "key:Y", "key:U" };

    private static List<string> Row(params string[] keys) => keys.ToList();

    /// <summary>默认方案：三行 21 键，升半音按左 Shift。</summary>
    public static InstrumentProfile Default()
    {
        var p = new InstrumentProfile
        {
            Id = "default",
            Name = "三行 21 键（默认）",
            Description = "Z-M 中音、A-J 高八度、Q-U 再高八度。升半音同时按左 Shift。"
                        + "对应多数游戏的乐器演奏模式。",
            NotesPerRow = 7,
            NoteOffsets = Major.ToList(),
            BasePitch = 60,
            SharpMode = SharpMode.Modifier,
            SharpModifier = "key:LEFTSHIFT",
            Rows = new List<ProfileRow>
            {
                new() { OctaveOffset = 0, Keys = Row(LowerRow) },
                new() { OctaveOffset = 1, Keys = Row(HomeRow) },
                new() { OctaveOffset = 2, Keys = Row(UpperRow) }
            }
        };
        p.Id = p.LayoutFingerprint();
        return p;
    }

    /// <summary>开箱可用的方案清单。名字只写键位与音域，不绑定具体游戏。</summary>
    public static List<InstrumentProfile> All()
    {
        var list = new List<InstrumentProfile> { Default() };

        list.Add(Make(
            "三行 21 键 · 无升半音",
            "同样三行 21 键，但乐器没有半音：所有 # 音自动降到最近的音级。",
            basePitch: 60,
            SharpModes.None,
            rows: new[] { (0, LowerRow), (1, HomeRow), (2, UpperRow) }));

        list.Add(Make(
            "三行 21 键 · 升半音 = 换行",
            "半音靠换到相邻的八度行去找。适合某些把半音放在另一行的乐器。",
            basePitch: 60,
            SharpMode.RowShift,
            rows: new[] { (0, LowerRow), (1, HomeRow), (2, UpperRow) }));

        list.Add(Make(
            "两行 15 键",
            "Z-M 中音、Q-U 高一个八度。升半音按左 Shift。",
            basePitch: 60,
            SharpMode.Modifier,
            rows: new[] { (0, LowerRow), (1, UpperRow) }));

        list.Add(Make(
            "两行 14 键 · 无升半音",
            "Z-M 中音、Q-U 高一个八度，乐器没有半音，# 音自动降级。",
            basePitch: 60,
            SharpModes.None,
            rows: new[] { (0, LowerRow), (1, UpperRow) }));

        list.Add(Make(
            "单行 7 键",
            "只有一个八度的乐器。超出音域的音自动空拍。",
            basePitch: 60,
            SharpModes.None,
            rows: new[] { (0, LowerRow) }));

        list.Add(Make(
            "10 键 · Z-M + Q W E",
            "低八度 Z-M 加高八度前三个音，升半音按左 Shift。",
            basePitch: 60,
            SharpMode.Modifier,
            rows: new[] { (0, LowerRow), (1, new[] { "key:Q", "key:W", "key:E" }) },
            notesPerRow: null));

        return list;
    }

    private static InstrumentProfile Make(
        string name,
        string description,
        int basePitch,
        SharpMode sharpMode,
        (int Offset, string[] Keys)[] rows,
        int? notesPerRow = null)
    {
        var p = new InstrumentProfile
        {
            Id = "",
            Name = name,
            Description = description,
            NotesPerRow = notesPerRow ?? 7,
            NoteOffsets = Major.ToList(),
            BasePitch = basePitch,
            SharpMode = sharpMode,
            SharpModifier = sharpMode == SharpMode.Modifier ? "key:LEFTSHIFT" : "",
            SharpRowOffset = sharpMode == SharpMode.RowShift ? 1 : 0,
            Rows = rows.Select(r => new ProfileRow
            {
                OctaveOffset = r.Offset,
                Keys = r.Keys.ToList()
            }).ToList()
        };
        p.Id = p.LayoutFingerprint();
        return p;
    }
}
