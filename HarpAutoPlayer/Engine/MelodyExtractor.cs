using HarpAutoPlayer.Midi;

namespace HarpAutoPlayer.Engine;

/// <summary>
/// 主旋律取音：把"伴奏 + 旋律混在一起"的多音轨/和弦声部压成最像原曲主旋律的单音线。
/// 不能只"同刻取最高"：伴奏在旋律下方（钢琴左手）时 100% 正确，但右手琶音常在主旋律之上，
/// 取最高会系统性取错音（用户反馈"吹 see you again 听不出原曲"即由此而来）。
/// 两步实现（实测把"伴奏在上方"场景正确率从 0% 提到 100%）：
///   ① 声部分离：按时间流式扫描，每个音归入"当前活跃且音高最接近"的声部，否则新开声部；
///      伴奏的高音琶音自成一"声部"，主旋律在另一条里保持完整。
///   ② 选声部：按「级进比例（最重要）/ 平均时值 / 音区 / 音域宽度 / 音符密度」打分，取最高分。
///   ③ 自动择一（安全阀）：最高声部级进比例很高且时值不短 → 主旋律本来就在最高声部，
///      直接沿用"同刻取最高"，既拿到声部分离收益，又不在简单曲子上退化。
/// </summary>
public static class MelodyExtractor
{
    // ---------------- 声部分离参数（都由合成曲实测调出，见 漏音分析/melody_voice_separation.py）----------------
    private const double Eps = 0.025;          // "同刻"判定窗口（与 NoteMapper 保持一致）
    private const double MaxGap = 0.12;        // 声部中断超过这么久 → 视为该声部已结束
    private const double MaxOverlap = 0.15;    // 与声部内延音中的音重叠超过这么久 → 不并入
    private const int MaxVoices = 6;           // 声部数上限，防止碎片化

    /// <summary>
    /// 一条声部（同一条旋律/伴奏线的音符序列，按时间升序）。
    /// </summary>
    private sealed class Voice
    {
        public readonly List<RawNote> Notes = new();
        public RawNote? Last;

        public double Start => Notes.Count == 0 ? 0 : Notes[0].Start;
        public double End => Notes.Count == 0 ? 0 : Notes[^1].End;

        public double MeanPitch
        {
            get
            {
                if (Notes.Count == 0) return 0;
                double s = 0;
                foreach (var n in Notes) s += n.Pitch;
                return s / Notes.Count;
            }
        }

        public double Span
        {
            get
            {
                if (Notes.Count == 0) return 0;
                int lo = int.MaxValue, hi = int.MinValue;
                foreach (var n in Notes)
                {
                    if (n.Pitch < lo) lo = n.Pitch;
                    if (n.Pitch > hi) hi = n.Pitch;
                }
                return hi - lo;
            }
        }

        public double MeanDuration
        {
            get
            {
                if (Notes.Count == 0) return 0;
                double s = 0;
                foreach (var n in Notes) s += Math.Max(0, n.End - n.Start);
                return s / Notes.Count;
            }
        }

        /// <summary>相邻音在小音程（≤4 半音）内的比例 —— 旋律最主要的特征。</summary>
        public double StepRatio
        {
            get
            {
                if (Notes.Count < 2) return 0;
                int step = 0;
                for (int i = 0; i + 1 < Notes.Count; i++)
                    if (Math.Abs(Notes[i + 1].Pitch - Notes[i].Pitch) <= 4) step++;
                return (double)step / (Notes.Count - 1);
            }
        }

        /// <summary>音符密度（每秒音符数）—— 伴奏常比旋律密。</summary>
        public double Density
        {
            get
            {
                double span = End - Start;
                return span <= 1e-6 ? Notes.Count : Notes.Count / span;
            }
        }
    }

    /// <summary>
    /// 分声部：返回按起始时间排序的声部列表，流式贪心归属，能并进活跃声部则并、否则新开。
    /// </summary>
    private static List<Voice> SeparateVoices(IEnumerable<RawNote> notes)
    {
        var sorted = notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
        var voices = new List<Voice>();

        foreach (var n in sorted)
        {
            int best = -1;
            double bestDist = double.MaxValue;

            for (int v = 0; v < voices.Count; v++)
            {
                var last = voices[v].Last;
                if (last == null) continue;

                // 声部已断
                if (n.Start - last.End > MaxGap) continue;

                // 与声部内仍在延音的音重叠太多 → 不适合并入
                if (last.End > n.Start + Eps && last.End - n.Start > MaxOverlap) continue;

                double d = Math.Abs(n.Pitch - last.Pitch);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = v;
                }
            }

            if (best < 0)
            {
                if (voices.Count >= MaxVoices)
                {
                    // 已达声部上限：并入"最近结束"的那条，避免碎成一片
                    int nearest = -1;
                    double nd = double.MaxValue;
                    for (int v = 0; v < voices.Count; v++)
                    {
                        var last = voices[v].Last;
                        if (last == null) continue;
                        double d = Math.Abs(n.Pitch - last.Pitch) + Math.Abs(n.Start - last.End) * 10;
                        if (d < nd) { nd = d; nearest = v; }
                    }
                    if (nearest >= 0)
                    {
                        voices[nearest].Notes.Add(n);
                        voices[nearest].Last = n;
                        continue;
                    }
                }
                var nv = new Voice();
                nv.Notes.Add(n);
                nv.Last = n;
                voices.Add(nv);
            }
            else
            {
                voices[best].Notes.Add(n);
                voices[best].Last = n;
            }
        }

        return voices.OrderBy(v => v.Start).ToList();
    }

    /// <summary>给每条声部打"像不像主旋律"的分。</summary>
    private static double ScoreVoice(Voice v, double allMean, double maxDur, double maxDensity)
    {
        double s = 0;
        s += v.StepRatio * 55;                                        // ① 级进：最重要
        s += (maxDur <= 0 ? 0 : v.MeanDuration / maxDur) * 25;         // ② 时值偏长
        s += Math.Max(0, (v.MeanPitch - (allMean - 6)) * 0.8);         // ③ 音区不低于平均太多
        s -= Math.Max(0, (v.Span - 19) * 1.2);                         // ④ 音域过宽（琶音）惩罚
        s -= (maxDensity <= 0 ? 0 : v.Density / maxDensity) * 12;      // ⑤ 密度过高（跑动伴奏）惩罚
        if (v.Notes.Count < 4) s -= 15;                                // 太碎不成句
        return s;
    }

    /// <summary>把同音高、时间上相连的音并成一个长音。</summary>
    private static List<RawNote> MergeSamePitch(IEnumerable<RawNote> notes)
    {
        var outList = new List<RawNote>();
        foreach (var n in notes.OrderBy(x => x.Start))
        {
            var last = outList.Count > 0 ? outList[^1] : null;
            if (last != null && last.Pitch == n.Pitch && n.Start <= last.End + Eps)
            {
                outList[^1] = new RawNote
                {
                    Pitch = last.Pitch,
                    Start = last.Start,
                    End = Math.Max(last.End, n.End),
                    Velocity = n.Velocity
                };
            }
            else
            {
                outList.Add(n);
            }
        }
        return outList;
    }

    /// <summary>同刻取最高（旧策略，保留作为简单曲子的更优解）。</summary>
    private static List<RawNote> HighestPerCluster(IEnumerable<RawNote> notes)
    {
        var s = notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
        var outList = new List<RawNote>();
        int i = 0;
        while (i < s.Count)
        {
            int j = i;
            while (j + 1 < s.Count && s[j + 1].Start - s[i].Start <= Eps) j++;
            outList.Add(s[j]);      // 组内最高
            i = j + 1;
        }
        return outList;
    }

    /// <summary>两条单音线的"音高一致率"，用于自动择一时判断两条线是否等价。</summary>
    private static double Agreement(IReadOnlyList<RawNote> a, IReadOnlyList<RawNote> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int hit = 0;
        foreach (var x in a)
            foreach (var y in b)
                if (x.Pitch == y.Pitch && Math.Abs(x.Start - y.Start) <= 0.08) { hit++; break; }
        return (double)hit / a.Count;
    }

    /// <summary>
    /// 提取主旋律线。返回按时间升序的单音序列。
    /// autoChoose=true 时启用"自动择一"安全阀（推荐）。
    /// </summary>
    public static List<RawNote> Extract(IEnumerable<RawNote> notes, bool autoChoose = true)
    {
        var list = notes.ToList();
        if (list.Count == 0) return list;

        // 单声部（本来就只有一个音在响）→ 无需处理
        var voices = SeparateVoices(list);
        if (voices.Count <= 1)
        {
            var only = voices.Count == 1 ? voices[0].Notes : list;
            return MergeSamePitch(only);
        }

        double allMean = voices.Average(v => v.MeanPitch);
        double maxDur = voices.Max(v => v.MeanDuration);
        double maxDensity = voices.Max(v => v.Density);

        int bestIdx = 0;
        double bestScore = double.MinValue;
        for (int i = 0; i < voices.Count; i++)
        {
            double s = ScoreVoice(voices[i], allMean, maxDur, maxDensity);
            if (s > bestScore) { bestScore = s; bestIdx = i; }
        }

        // 安全阀：最高声部本身高度级进、时值不短 → 主旋律本来就在最高声部，
        // 此时"同刻取最高"对「伴奏在下方」的曲子 100% 正确，直接沿用更稳。
        if (autoChoose)
        {
            int topIdx = 0;
            double topMean = double.MinValue;
            double avgDur = voices.Average(v => v.MeanDuration);
            for (int i = 0; i < voices.Count; i++)
                if (voices[i].MeanPitch > topMean) { topMean = voices[i].MeanPitch; topIdx = i; }

            if (topIdx == bestIdx && voices[topIdx].StepRatio >= 0.85 &&
                voices[topIdx].MeanDuration >= avgDur)
                return HighestPerCluster(list);
        }

        return MergeSamePitch(voices[bestIdx].Notes);
    }
}
