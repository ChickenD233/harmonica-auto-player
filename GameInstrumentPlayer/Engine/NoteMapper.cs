using GameInstrumentPlayer.Midi;
using GameInstrumentPlayer.Profiles;

namespace GameInstrumentPlayer.Engine;

/// <summary>映射后的单个音符：要发的琴键、要按的修饰键、以及它落在哪一行。</summary>
public sealed class MappedNote
{
    public int Pitch { get; init; }            // 移调后的音高
    public double Start { get; init; }         // 秒（未乘速度）
    public double End { get; init; }
    public NoteAction Key { get; init; }       // 琴键
    public NoteAction Modifier { get; init; }  // 升半音要同时按住的键
    public int RowIndex { get; init; }         // 落在第几行（0 = 基准行）
    public int RowOctaveOffset { get; init; }  // 该行相对基准八度的偏移
    public bool InRange { get; init; }         // false → 方案弹不出，空拍跳过
    public bool Adjusted { get; init; }        // 音高被改过（半音降到自然音）
    public bool Sharp { get; init; }           // 这个音需要修饰键
    public string SkipReason { get; init; } = "";

    public bool HasModifier => !Modifier.IsNone;
}

/// <summary>一次映射的结果。</summary>
public sealed class MappingResult
{
    /// <summary>本次使用的基准音高（第一行第一个音的 MIDI 编号）。</summary>
    public int BasePitch { get; set; }
    public List<MappedNote> Notes { get; init; } = new();
    public int InRangeCount => Notes.Count(n => n.InRange);
    public int SkipCount => Notes.Count(n => !n.InRange);
    public int AdjustedCount => Notes.Count(n => n.Adjusted && n.InRange);
}

/// <summary>
/// 把旋律音高映射到当前乐器方案的琴键上。
/// 音高 → 行（八度档位）→ 该行的第几个键；半音按方案的「升半音方式」处理。
/// </summary>
public static class NoteMapper
{
    /// <summary>主键盘上可以当琴键用的字符。</summary>
    public static readonly char[] KeyChars =
        "1234567890QWERTYUIOPASDFGHJKLZXCVBNM".ToCharArray();

    /// <summary>这次映射用到的方案（null 时保持上一次设置）。</summary>
    public static InstrumentProfile Profile { get; set; } = BuiltInProfiles.Default();

    /// <summary>一个音高落在哪一行、是这一行的第几个键、以及离该行音级差几半音。</summary>
    private readonly record struct Placement(int RowIndex, int KeyIndex, int Delta)
    {
        public static Placement Miss => new(-1, -1, 0);
        public bool Found => RowIndex >= 0;
    }

    /// <summary>
    /// 按八度把音高定位到某一行：
    /// KeyIndex 是这一行里离它最近的琴键下标；Delta 是该琴键与音高的半音差（0 = 正好是这个音）。
    /// 定位不到行（超出音域）返回 <see cref="Placement.Miss"/>。
    /// </summary>
    private static Placement Locate(InstrumentProfile profile, int basePitch, int pitch)
    {
        // 枚举所有可能：每个八度行里的每一把琴键，算出它离这个音高差几半音。
        // 然后挑「差得最少」的那个；一样近就留先到的那个（音级表按升序，先到的音更低）。
        Placement best = Placement.Miss;

        for (int i = 0; i < profile.Rows.Count; i++)
        {
            var row = profile.Rows[i];
            if (row.Keys.Count == 0) continue;

            int root = basePitch + row.OctaveOffset * 12;
            // 一行只覆盖自己的那一个八度。不限制范围的话，任何八度的同名音都会落到第一行。
            int rel = pitch - root;
            if (rel < 0 || rel > 11) continue;

            for (int k = 0; k < profile.NoteOffsets.Count; k++)
            {
                int delta = rel - profile.NoteOffsets[k];
                int keyIdx = Math.Min(k, row.Keys.Count - 1);   // 该行键少时归到最后一个键

                if (!best.Found)
                {
                    best = new Placement(i, keyIdx, delta);
                    continue;
                }

                // 更近就换；一样近保留已选中的（先到的音级更低）
                if (Math.Abs(delta) < Math.Abs(best.Delta))
                    best = new Placement(i, keyIdx, delta);
            }
        }

        return best;
    }

    /// <summary>自选基准音高：让尽量多的音落进方案音域。</summary>
    public static int AutoBasePitch(InstrumentProfile profile, IReadOnlyList<int> pitches)
    {
        if (pitches.Count == 0) return profile.BasePitch;

        int minO = int.MaxValue, maxO = int.MinValue;
        double sumO = 0;
        foreach (var p in pitches)
        {
            int o = p / 12;
            if (o < minO) minO = o;
            if (o > maxO) maxO = o;
            sumO += o;
        }
        double meanO = sumO / pitches.Count;

        int span = Math.Max(1, profile.AutoOctaveRange);
        int bestB = profile.BasePitch;
        int bestPlay = -1;
        double bestDist = double.MaxValue;

        for (int k = -span; k <= span; k++)
        {
            int b = profile.BasePitch + k * 12;
            int play = pitches.Count(p => Locate(profile, b, p).RowIndex >= 0);
            double dist = Math.Abs(b / 12.0 - meanO);
            if (play > bestPlay || (play == bestPlay && dist < bestDist))
            {
                bestPlay = play;
                bestDist = dist;
                bestB = b;
            }
        }
        return bestB;
    }

    /// <summary>
    /// 执行映射。notes 为旋律原始音符；transpose 为整体移调半音数；
    /// manualBasePitch 为手动基准音高，null 表示自动。
    /// </summary>
    public static MappingResult Map(
        IReadOnlyList<RawNote> notes,
        int transpose,
        int? manualBasePitch = null,
        InstrumentProfile? profile = null)
    {
        profile ??= Profile;
        var result = new MappingResult();

        var valid = new List<int>();
        foreach (var n in notes)
        {
            int p = n.Pitch + transpose;
            if (p is >= 0 and <= 127) valid.Add(p);
        }

        int basePitch = manualBasePitch ?? AutoBasePitch(profile, valid);
        result.BasePitch = basePitch;

        foreach (var n in notes)
        {
            int p = n.Pitch + transpose;
            if (p is < 0 or > 127)
            {
                result.Notes.Add(new MappedNote
                {
                    Pitch = p, Start = n.Start, End = n.End,
                    InRange = false, SkipReason = "移调后超出 MIDI 音域"
                });
                continue;
            }

            // 先按八度找到所在的行，并算出「离本行最近的音级」差多少半音。
            // delta != 0 就说明这是个半音，具体怎么处理交给方案的「升半音方式」，不在这里判空拍。
            var place = Locate(profile, basePitch, p);
            if (!place.Found)
            {
                result.Notes.Add(new MappedNote
                {
                    Pitch = p, Start = n.Start, End = n.End,
                    InRange = false,
                    SkipReason = $"超出本方案音域（{profile.RangeLabel}）"
                });
                continue;
            }

            bool offScale = place.Delta != 0;
            var modifier = NoteAction.None;
            // playedShift = 实际响的音比原音低几个半音。三种处理方式都按它记账，
            // 保证 MappedNote.Pitch 与真正按下的那把琴键一致。
            int playedShift = Math.Abs(place.Delta);
            int finalRow = place.RowIndex;
            int keyIndex = place.KeyIndex;
            bool adjusted = false;

            if (offScale)
            {
                switch (profile.SharpMode)
                {
                    case SharpMode.Modifier:
                        modifier = ActionCodec.Parse(profile.SharpModifier);
                        if (modifier.IsNone)
                        {
                            result.Notes.Add(new MappedNote
                            {
                                Pitch = p, Start = n.Start, End = n.End,
                                InRange = false, Sharp = true,
                                SkipReason = "方案没有指定升半音修饰键"
                            });
                            continue;
                        }
                        // 修饰键 + 本行较低的琴键，音高靠修饰键升回去
                        break;

                    case SharpMode.RowShift:
                    {
                        int shifted = p + profile.SharpRowOffset * 12;
                        var place2 = Locate(profile, basePitch, shifted);
                        if (!place2.Found)
                        {
                            result.Notes.Add(new MappedNote
                            {
                                Pitch = p, Start = n.Start, End = n.End,
                                InRange = false, Sharp = true,
                                SkipReason = "升半音换行后超出音域"
                            });
                            continue;
                        }
                        finalRow = place2.RowIndex;
                        keyIndex = place2.KeyIndex;
                        playedShift = -place2.Delta;   // 换行后离原音的总偏移
                        adjusted = true;
                        break;
                    }

                    case SharpMode.Skip:
                        result.Notes.Add(new MappedNote
                        {
                            Pitch = p, Start = n.Start, End = n.End,
                            InRange = false, Sharp = true,
                            SkipReason = "本乐器没有半音（已按方案跳过）"
                        });
                        continue;

                    default:   // Snap：降到本行较低的琴键
                        adjusted = true;
                        break;
                }
            }

            var row = profile.Rows[finalRow];
            var action = ActionCodec.Parse(row.Keys[Math.Clamp(keyIndex, 0, row.Keys.Count - 1)]);
            int playedPitch = p - playedShift;

            result.Notes.Add(new MappedNote
            {
                Pitch = playedPitch,
                Start = n.Start,
                End = n.End,
                Key = action,
                Modifier = modifier,
                RowIndex = finalRow,
                RowOctaveOffset = row.OctaveOffset,
                Sharp = offScale && profile.SharpMode == SharpMode.Modifier,
                Adjusted = adjusted,
                InRange = true
            });
        }

        return result;
    }

    /// <summary>这个方案当前能演奏的全部音高，供卷帘上色用。</summary>
    public static IReadOnlyList<int> PlayablePitches(InstrumentProfile? profile = null)
    {
        var p = profile ?? Profile;
        return p.PlayablePitches(p.BasePitch);
    }

    /// <summary>界面用的单音描述，如「C4(1) → 按[Z]」。带修饰键时写「+Shift」。</summary>
    public static string Describe(MappedNote n, bool withTime)
    {
        string time = withTime ? $"{n.Start:F2}s " : "";
        string mod = n.HasModifier ? $"+{n.Modifier.Label}" : "";
        return $"{time}{Music.NoteName(n.Pitch)}({Music.DegreeName(n.Pitch)}) → 按[{mod}{n.Key.Label}]";
    }

    // =====================================================================
    // 旋律提取工具（与具体乐器无关）
    // =====================================================================

    /// <summary>
    /// 人声歌的「旋律提取」（伴奏与主唱混在同一轨时用）：按人声音区 + 顶音 + 连续性三条线索挑出最像人声主旋律的线。
    /// 只在人声音区（默认 D3~D6）内选音；同刻多音取最高；下一步尽量贴着上一步走，离人声音区很远的低音/和声伴奏自动排除。
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
    /// 单音化（多数游戏乐器一次只发一个音），目标尽量还原原曲主旋律。
    /// 同刻（±25ms）多音一起响时只留【最高】音：流行编曲主旋律/主声部通常在最高声部，
    /// 取最高最贴近原曲走向；不做「找最近、保持不动」之类平滑（那会改变原曲旋律）。
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
    /// 多声部合奏合成单音线：同刻多个声部一起响时只保留编号最小（Rank 最小）的声部；
    /// 低优先级音压在高优先级音尾音上 → 该段让位。
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
        // 1) 同刻组内选 Rank 最小者；被压掉的低优先级音若更长，「超出主声部结束」的尾巴稍后补回。
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

    /// <summary>
    /// 整体平移旋律，使首音从 0 秒开始（音间相对时值不变）；很多 MIDI 开头有几小节休止，剪掉后点播放立刻出音。
    /// 首音 ≈0s 时原样返回。
    /// </summary>
    public static List<RawNote> TrimLeadingSilence(IReadOnlyList<RawNote> notes)
    {
        if (notes.Count == 0) return new List<RawNote>();
        double first = notes.Min(n => n.Start);
        if (first <= 0.001) return notes.ToList();

        return notes.Select(n => new RawNote
        {
            Pitch = n.Pitch,
            Start = Math.Max(0, n.Start - first),
            End = Math.Max(0, n.End - first),
            Velocity = n.Velocity
        }).ToList();
    }
}
