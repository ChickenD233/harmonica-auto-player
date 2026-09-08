using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace HarpAutoPlayer.Midi;

/// <summary>
/// 负责导入并“识别”各种标准 MIDI 文件（格式 0 / 1 / 2 均可），
/// 按 (轨道, 声道) 拆出可选的旋律候选，并换算为秒。
/// </summary>
public static class MidiLoader
{
    public static ParsedMidi Parse(string path)
    {
        // 先整体读入内存再解析：避免 iCloud/网络盘“占位文件”、文件占用等导致解析与读取互相干扰
        byte[] data = File.ReadAllBytes(path);
        using var stream = new MemoryStream(data);
        // 用 UTF-8 解码轨道名等文本，避免中文显示为 ?
        var settings = new ReadingSettings { TextEncoding = System.Text.Encoding.UTF8 };
        var file = MidiFile.Read(stream, settings);
        var tempoMap = file.GetTempoMap();

        string divisionLabel;
        switch (file.TimeDivision)
        {
            case TicksPerQuarterNoteTimeDivision td:
                divisionLabel = $"{td.TicksPerQuarterNote} 刻/四分音符";
                break;
            case SmpteTimeDivision sd:
                divisionLabel = $"SMPTE {sd.Format} {sd.Resolution}";
                break;
            default:
                divisionLabel = "未知";
                break;
        }

        var candidates = new List<MidiCandidate>();
        int trackIdx = 0;
        double fileEndSec = 0;

        foreach (var chunk in file.GetTrackChunks())
        {
            string trackName = chunk.Events.OfType<SequenceTrackNameEvent>()
                                      .FirstOrDefault()?.Text ?? "";

            var allNotes = chunk.GetNotes().ToList();

            foreach (var grp in allNotes.GroupBy(n => (int)n.Channel))
            {
                var notes = new List<RawNote>();
                foreach (var n in grp)
                {
                    double s = TicksToSeconds(tempoMap, n.Time);
                    double e = TicksToSeconds(tempoMap, n.EndTime);
                    if (e - s < 0.02) e = s + 0.02; // 极短音保证至少 20ms

                    notes.Add(new RawNote
                    {
                        Pitch = n.NoteNumber,
                        Start = s,
                        End = e,
                        Velocity = n.Velocity
                    });
                    if (e > fileEndSec) fileEndSec = e;
                }

                if (notes.Count == 0) continue;

                var cand = new MidiCandidate
                {
                    TrackIndex = trackIdx,
                    Channel = grp.Key,
                    Name = BuildCandidateName(trackName, grp.Key),
                    Notes = notes,
                    DurationSec = notes.Max(x => x.End) - notes.Min(x => x.Start)
                };
                candidates.Add(cand);
            }

            trackIdx++;
        }

        return new ParsedMidi
        {
            FilePath = path,
            DivisionLabel = divisionLabel,
            DurationSec = fileEndSec,
            Candidates = candidates
        };
    }

    private static string BuildCandidateName(string trackName, int channel)
    {
        if (!string.IsNullOrWhiteSpace(trackName))
            return trackName.Trim();
        return channel == 9 ? "打击乐" : $"声道 {channel + 1}";
    }

    private static double TicksToSeconds(TempoMap tempoMap, long ticks)
    {
        var metric = TimeConverter.ConvertTo<MetricTimeSpan>(ticks, tempoMap);
        return metric.TotalSeconds;
    }
}
