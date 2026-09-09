using HarpAutoPlayer.Midi;

namespace HarpAutoPlayer.Engine;

/// <summary>八度档位：相对基准八度的偏移。</summary>
public enum Slot
{
    Low = -1,   // 比基准低一个八度 → 按住鼠标左键
    Mid = 0,    // 基准八度     → 不按鼠标
    High = 1    // 比基准高一个八度 → 按住鼠标右键
}

/// <summary>映射后的单个可演奏音符。</summary>
public sealed class MappedNote
{
    public int Pitch { get; init; }
    public double Start { get; init; }   // 秒（未乘速度）
    public double End { get; init; }
    public char Key { get; init; }       // 'Z'..'M'
    public bool Sharp { get; init; }     // true → 需按住鼠标中键（升半音）
    public Slot OctaveSlot { get; init; }
    public bool InRange { get; init; }   // false → 超出三个八度，空拍跳过
    public string SkipReason { get; init; } = "";
}

/// <summary>一次映射的结果。</summary>
public sealed class MappingResult
{
    public int BaseOctave { get; set; }        // 基准八度（MIDI 编号，C4=第4八度）
    public List<MappedNote> Notes { get; init; } = new();
    public int InRangeCount => Notes.Count(n => n.InRange);
    public int SkipCount => Notes.Count(n => !n.InRange);
}

/// <summary>
/// 把主旋律 MIDI 音高映射到游戏口琴的按键方案：
///  一个八度内 do..ti → 键盘 z x c v b n m；
///  带 # 的音（升半音）→ 同时按住鼠标中键；
///  三个八度 → 不按鼠标=基准、按住左键=低八度、按住右键=高八度；
///  超出三个八度的音 → 空拍（不吹）。
/// </summary>
public static class NoteMapper
{
    /// <summary>do..ti 对应的键位（z x c v b n m）。</summary>
    public static readonly char[] Keys = { 'Z', 'X', 'C', 'V', 'B', 'N', 'M' };

    /// <summary>高高音do 用的键：键盘逗号“，”。</summary>
    public const char TopKey = ',';

    /// <summary>某个音高在“基准=baseOct”下是否可演奏。
    /// 可演奏区 = 基准±1 的完整两个/三个八度，外加最上方 高高音do / 高高音#do。</summary>
    private static bool Reachable(int pitch, int baseOct)
    {
        int d = pitch / 12 - 1 - baseOct;
        if (d is >= -1 and <= 1) return true;
        if (d == 2)
        {
            int pc = Music.Mod(pitch, 12);
            return pc is 0 or 1;   // 高高音do / 高高音#do（右键+逗号，可再加中键）
        }
        return false;
    }

    /// <summary>自然音（无升降）对应的半音号。</summary>
    private static readonly int[] NaturalPc = { 0, 2, 4, 5, 7, 9, 11 };

    private static readonly HashSet<int> SharpPc = new() { 1, 3, 6, 8, 10 };

    private static int DiatonicIndexOf(int pitchClass) => pitchClass switch
    {
        0 => 0, 2 => 1, 4 => 2, 5 => 3, 7 => 4, 9 => 5, 11 => 6,
        _ => -1
    };

    /// <summary>取任意音高的键位：先按半音号归到最近的自然音，再给 z..m 键。</summary>
    public static char KeyOfPitch(int pitch)
    {
        int pc = Music.Mod(pitch, 12);
        int idx = DiatonicIndexOf(pc);
        if (idx < 0)
        {
            // 升号音：降半音后取自然音
            pc = Music.Mod(pc - 1, 12);
            idx = DiatonicIndexOf(pc);
        }
        return Keys[idx];
    }

    /// <summary>该音高是否属于“向上的半音”（需要中键）。</summary>
    public static bool IsSharpPitch(int pitch) => SharpPc.Contains(Music.Mod(pitch, 12));

    /// <summary>自动选出基准八度，使可演奏区（基准±1八度 + 高高音do/#do）容纳最多音符。</summary>
    public static int AutoBaseOctave(IReadOnlyList<int> pitches)
    {
        if (pitches.Count == 0) return 4;

        int minO = int.MaxValue, maxO = int.MinValue;
        double sumO = 0;
        foreach (var p in pitches)
        {
            int o = p / 12 - 1;
            if (o < minO) minO = o;
            if (o > maxO) maxO = o;
            sumO += o;
        }
        double meanO = sumO / pitches.Count;

        int bestB = minO;
        int bestPlay = -1;
        for (int b = minO; b <= maxO; b++)
        {
            int play = pitches.Count(p => Reachable(p, b));
            if (play > bestPlay ||
                (play == bestPlay && Math.Abs(b - meanO) < Math.Abs(bestB - meanO)))
            {
                bestPlay = play;
                bestB = b;
            }
        }
        return bestB;
    }

    /// <summary>
    /// 执行映射。
    /// </summary>
    /// <param name="notes">主旋律原始音符。</param>
    /// <param name="transpose">整体移调半音数（-24..+24）。</param>
    /// <param name="manualBaseOctave">手动基准八度；null 表示自动。</param>
    public static MappingResult Map(IReadOnlyList<RawNote> notes, int transpose, int? manualBaseOctave)
    {
        var result = new MappingResult();

        var valid = new List<int>();
        foreach (var n in notes)
        {
            int p = n.Pitch + transpose;
            if (p >= 0 && p <= 127) valid.Add(p);
        }

        int baseOctave = manualBaseOctave ?? AutoBaseOctave(valid);
        result.BaseOctave = baseOctave;

        foreach (var n in notes)
        {
            int p = n.Pitch + transpose;
            if (p < 0 || p > 127)
            {
                result.Notes.Add(new MappedNote
                {
                    Pitch = p, Start = n.Start, End = n.End,
                    Key = ' ', Sharp = false, OctaveSlot = Slot.Mid,
                    InRange = false, SkipReason = "移调后超出 MIDI 音域"
                });
                continue;
            }

            int oct = p / 12 - 1;
            int diff = oct - baseOctave;

            if (diff < -1 || diff > 2)
            {
                result.Notes.Add(new MappedNote
                {
                    Pitch = p, Start = n.Start, End = n.End,
                    Key = KeyOfPitch(p), Sharp = IsSharpPitch(p),
                    OctaveSlot = Slot.Mid,
                    InRange = false,
                    SkipReason = $"音区超出口琴可演奏范围(第{oct}八度)"
                });
                continue;
            }

            if (diff == 2)
            {
                // 最上方只有两个音：高高音do、高高音#do（右键 + 逗号，带#再加中键）
                int pc = Music.Mod(p, 12);
                if (pc is not (0 or 1))
                {
                    result.Notes.Add(new MappedNote
                    {
                        Pitch = p, Start = n.Start, End = n.End,
                        Key = TopKey, Sharp = pc == 1,
                        OctaveSlot = Slot.High,
                        InRange = false,
                        SkipReason = "最高只能到 高高音#do"
                    });
                    continue;
                }
                result.Notes.Add(new MappedNote
                {
                    Pitch = p,
                    Start = n.Start,
                    End = n.End,
                    Key = TopKey,
                    Sharp = pc == 1,
                    OctaveSlot = Slot.High,
                    InRange = true
                });
                continue;
            }

            result.Notes.Add(new MappedNote
            {
                Pitch = p,
                Start = n.Start,
                End = n.End,
                Key = KeyOfPitch(p),
                Sharp = IsSharpPitch(p),
                OctaveSlot = (Slot)diff,
                InRange = true
            });
        }

        return result;
    }

    /// <summary>
    /// 人声歌的“旋律提取”（伴奏和主唱混在同一轨时用）：
    /// 人声音区 + 顶音 + 连续性三条线索，把最像人声主旋律的那条线挑出来。
    /// 规则：只在人声音区（默认 D3~D6）里选音；同刻多音取最高；下一步尽量贴着上一步走，
    /// 明显离人声音区很远的是低音/和声伴奏 → 自动排除。
    /// </summary>
    public static List<RawNote> ExtractVocalMelody(IEnumerable<RawNote> notes)
    {
        const int vocalLow = 50;    // D3
        const int vocalHigh = 86;   // D6
        const double eps = 0.025;

        var sorted = notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
        var keep = new List<RawNote>();
        int i = 0;
        int prevPitch = -1;
        while (i < sorted.Count)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1].Start - sorted[i].Start <= eps) j++;

            var group = sorted.GetRange(i, j - i + 1);
            var candidates = group.Where(n => n.Pitch >= vocalLow && n.Pitch <= vocalHigh).ToList();
            if (candidates.Count == 0) candidates = group;   // 该瞬无人声音区音 → 兜底

            RawNote chosen;
            if (prevPitch < 0)
            {
                chosen = candidates[^1];                    // 开头取人声音区里最高
            }
            else
            {
                // 连续性：优先选离上一步 ≤5 半音的音（这样主旋律是连续线）；多个都近就取最高的
                var close = candidates.Where(n => Math.Abs(n.Pitch - prevPitch) <= 5).ToList();
                if (close.Count > 0)
                    chosen = close[^1];                     // close 也按音高升序
                else
                    chosen = candidates
                        .OrderBy(n => Math.Abs(n.Pitch - prevPitch))
                        .ThenByDescending(n => n.Pitch)
                        .First();
            }

            keep.Add(chosen);
            prevPitch = chosen.Pitch;
            i = j + 1;
        }
        return keep;
    }

    /// <summary>
    /// 单音化（口琴一次只能吹一个音）——目标：尽量还原原曲主旋律。
    /// 同一瞬间（±25ms）多个音一起响时，只吹其中【最高】的那个音：
    /// 流行编曲里主旋律/主声部通常在最高声部，取最高音最贴近原曲的旋律走向。
    /// 不做“找最近、保持不动”之类的平滑（那会改变原曲旋律）。
    /// </summary>
    public static List<RawNote> ChordRootOnly(IEnumerable<RawNote> notes)
    {
        var sorted = notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
        const double eps = 0.025;
        var keep = new List<RawNote>();
        int i = 0;
        while (i < sorted.Count)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1].Start - sorted[i].Start <= eps) j++;

            var group = sorted.GetRange(i, j - i + 1);
            keep.Add(group[^1]);          // 最高音（group 已按音高升序）
            i = j + 1;
        }
        return keep;
    }

    /// <summary>
    /// 多声部合奏合成单音线（按优先级）：
    /// 同一瞬间多个声部同时响 → 只保留编号最小（Rank 最小）的声部；
    /// 低优先级音若压在高优先级音的尾音上 → 该段让位（省略低优先级音）。
    /// </summary>
    public static List<RawNote> MergeVoicesByPriority(IEnumerable<(int Rank, RawNote Note)> voices)
    {
        var ordered = voices
            .OrderBy(v => v.Note.Start)
            .ThenBy(v => v.Rank)
            .ThenBy(v => v.Note.Pitch)
            .ToList();
        if (ordered.Count == 0) return new List<RawNote>();

        const double eps = 0.025;
        // 1) 同一瞬间组内：选优先级最小（Rank 小）者；
        //    同时刻被压掉的低优先级音若更长，其“超出主声部结束”的尾巴要保留，稍后补回。
        var items = new List<(int Rank, RawNote Note)>();
        int i = 0;
        while (i < ordered.Count)
        {
            int j = i;
            double s0 = ordered[i].Note.Start;
            while (j + 1 < ordered.Count && ordered[j + 1].Note.Start - s0 <= eps) j++;

            var best = ordered[i];
            for (int k = i + 1; k <= j; k++)
            {
                if (ordered[k].Rank < best.Rank) best = ordered[k];
            }
            items.Add(best);
            for (int k = i; k <= j; k++)
            {
                var o = ordered[k];
                if (o.Note.End > best.Note.End && o.Rank > best.Rank)
                {
                    items.Add((o.Rank, new RawNote
                    {
                        Pitch = o.Note.Pitch,
                        Start = best.Note.End,          // 主声部结束后才轮到它
                        End = o.Note.End,
                        Velocity = o.Note.Velocity
                    }));
                }
            }
            i = j + 1;
        }

        // 2) 排序后统一压制：低优先级音落在高优先级音持续期间 → 让位（超出部分补尾巴）
        items = items.OrderBy(v => v.Note.Start).ThenBy(v => v.Rank).ThenBy(v => v.Note.Pitch).ToList();
        var result = new List<RawNote>();
        RawNote? lastNote = null;
        int lastRank = int.MaxValue;
        foreach (var c in items)
        {
            if (lastNote != null && c.Note.Start < lastNote.End && c.Rank > lastRank)
            {
                if (c.Note.End > lastNote.End)
                {
                    result.Add(new RawNote
                    {
                        Pitch = c.Note.Pitch,
                        Start = lastNote.End,
                        End = c.Note.End,
                        Velocity = c.Note.Velocity
                    });
                }
                continue;
            }
            result.Add(c.Note);
            lastNote = c.Note;
            lastRank = c.Rank;
        }

        return result.OrderBy(n => n.Start).ToList();
    }

    /// <summary>给界面用的单音描述。</summary>
    public static string Describe(MappedNote n, bool withTime)
    {
        string slot = n.OctaveSlot switch
        {
            Slot.Low => "低八度(按左键)",
            Slot.High => "高八度(按右键)",
            _ => "基准八度"
        };
        string keyShow = n.Key == TopKey ? "，" : n.Key.ToString();
        string sharp = n.Sharp ? "+升半音(按中键) " : "";
        string time = withTime ? $"{n.Start:F2}s " : "";
        return $"{time}{Music.NoteName(n.Pitch)}({Music.DegreeName(n.Pitch)}) → 按[{keyShow}] {sharp}{slot}";
    }
}
