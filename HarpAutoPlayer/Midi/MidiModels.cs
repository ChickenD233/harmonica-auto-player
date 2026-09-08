namespace HarpAutoPlayer.Midi;

/// <summary>MIDI 文件解析后的一个音符（已换算成秒）。</summary>
public sealed class RawNote
{
    public int Pitch { get; init; }        // MIDI 音高 0..127, 60 = C4
    public double Start { get; init; }     // 起始秒
    public double End { get; init; }       // 结束秒
    public int Velocity { get; init; }

    public override string ToString() => $"{Music.NoteName(Pitch)} {Start:F2}s~{End:F2}s";
}

/// <summary>一个可被选作主旋律的候选 = (轨道, 声道)。</summary>
public sealed class MidiCandidate
{
    public int TrackIndex { get; init; }   // 0 起
    public int Channel { get; init; }      // 0 起（MIDI 内部声道编号）
    public string Name { get; init; } = "";
    public List<RawNote> Notes { get; init; } = new();
    public double DurationSec { get; set; }

    public int NoteCount => Notes.Count;
    public int MinPitch => Notes.Count == 0 ? 0 : Notes.Min(n => n.Pitch);
    public int MaxPitch => Notes.Count == 0 ? 0 : Notes.Max(n => n.Pitch);

    public string ChannelLabel => Channel == 9 ? $"{Channel + 1}(打击乐)" : (Channel + 1).ToString();
    public string RangeLabel => Notes.Count == 0 ? "-"
        : $"{Music.NoteName(MinPitch)}~{Music.NoteName(MaxPitch)}";
    public string TrackLabel => (TrackIndex + 1).ToString();
}

/// <summary>解析后的整份 MIDI 文件。</summary>
public sealed class ParsedMidi
{
    public string FilePath { get; init; } = "";
    public string DivisionLabel { get; init; } = "";
    public double DurationSec { get; init; }
    public List<MidiCandidate> Candidates { get; init; } = new();
}

/// <summary>MIDI 音高相关的命名工具。</summary>
public static class Music
{
    private static readonly string[] Names =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static readonly string[] JianPu =
        { "1", "#1", "2", "#2", "3", "4", "#4", "5", "#5", "6", "#6", "7" };

    /// <summary>标准音名，如 C4 / F#5。</summary>
    public static string NoteName(int pitch) => $"{Names[Mod(pitch, 12)]}{pitch / 12 - 1}";

    /// <summary>简谱记号（含升降号），音高模 12。</summary>
    public static string DegreeName(int pitch) => JianPu[Mod(pitch, 12)];

    public static int Mod(int a, int b) => ((a % b) + b) % b;
}
