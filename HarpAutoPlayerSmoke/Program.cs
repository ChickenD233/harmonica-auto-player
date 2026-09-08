using System.Text;
using HarpAutoPlayer.Engine;
using HarpAutoPlayer.Midi;

// ---------- 手写一个最小的 SMF（格式1, 480 刻/四分音符, 120BPM） ----------
static void WriteBE16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
static void WriteBE32(Stream s, int v)
{
    s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
    s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
}
static void WriteVar(List<byte> s, long v)
{
    var buf = new List<byte>();
    buf.Add((byte)(v & 0x7F));
    while ((v >>= 7) > 0) { buf.Insert(0, (byte)((v & 0x7F) | 0x80)); }
    foreach (var b in buf) s.Add(b);
}
static void TrackChunk(Stream s, IEnumerable<byte> data)
{
    var bytes = data.ToArray();
    s.Write(Encoding.ASCII.GetBytes("MTrk"));
    WriteBE32(s, bytes.Length);
    s.Write(bytes);
}

// 主旋律：C5 E5 G5 F#5 B5 C4 C8，每音 1 拍（含故意超音域的 C8）
int[] pitches = { 72, 76, 79, 78, 83, 60, 108 };
var track1 = new List<byte>();
bool first = true;
foreach (var p in pitches)
{
    WriteVar(track1, first ? 0 : 0);          // 上一音的 NoteOff 已到拍点
    track1.Add(0x90); track1.Add((byte)p); track1.Add(100);
    WriteVar(track1, 480);
    track1.Add(0x80); track1.Add((byte)p); track1.Add(0);
    first = false;
}
track1.Add(0); track1.Add(0xFF); track1.Add(0x2F); track1.Add(0);

var ms = new MemoryStream();
ms.Write(Encoding.ASCII.GetBytes("MThd"));
WriteBE32(ms, 6); WriteBE16(ms, 1); WriteBE16(ms, 2); WriteBE16(ms, 480);
var tempo = new List<byte>();
WriteVar(tempo, 0); tempo.Add(0xFF); tempo.Add(0x51); tempo.Add(0x03);
tempo.Add(0x07); tempo.Add(0xA1); tempo.Add(0x20);   // tempo 500000us
WriteVar(tempo, 0); tempo.Add(0xFF); tempo.Add(0x2F); tempo.Add(0);
TrackChunk(ms, tempo);
TrackChunk(ms, track1);

string path = Path.Combine(Path.GetTempPath(), "smoke_melody.mid");
File.WriteAllBytes(path, ms.ToArray());
Console.WriteLine($"已生成测试 MIDI: {path} ({new FileInfo(path).Length} 字节)");

// ---------- 1) 载入解析（走 MidiLoader 真实代码） ----------
var parsed = MidiLoader.Parse(path);
Console.WriteLine($"候选数={parsed.Candidates.Count}  时长≈{parsed.DurationSec:F2}s  划分={parsed.DivisionLabel}");
if (parsed.Candidates.Count == 0) throw new Exception("没有候选!");
var cand = parsed.Candidates[0];
Console.WriteLine($"候选: 轨{cand.TrackIndex + 1} 声道{cand.Channel + 1} 音符{cand.NoteCount} 音域{cand.RangeLabel}");
if (cand.NoteCount != 7) throw new Exception($"音符数错误: {cand.NoteCount}");

// 时间换算验证：120BPM 每拍 0.5s → 依次 0,0.5,1,1.5,2,2.5,3（允差 20ms）
double[] expectStart = { 0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0 };
for (int i = 0; i < cand.Notes.Count; i++)
{
    double got = cand.Notes[i].Start;
    if (Math.Abs(got - expectStart[i]) > 0.02)
        throw new Exception($"第{i}音起始时间错误: got {got:F3}, expect {expectStart[i]}");
}
Console.WriteLine("时间换算 OK（每音 0.5s 起始）");

// ---------- 2) 映射（自动基准八度） ----------
var map = NoteMapper.Map(cand.Notes, 0, null);
Console.WriteLine($"基准八度={map.BaseOctave}  可吹={map.InRangeCount}  空拍={map.SkipCount}");
foreach (var n in map.Notes)
    Console.WriteLine("  " + NoteMapper.Describe(n, withTime: true));

var fsharp = map.Notes.First(n => n.Pitch == 78); // F#5 → V + 中键
if (fsharp.Key != 'V' || !fsharp.Sharp) throw new Exception("F# 映射错误");
var b5 = map.Notes.First(n => n.Pitch == 83);     // B5 → M（ti），无升降
if (b5.Key != 'M' || b5.Sharp) throw new Exception("B 映射错误");
var c4 = map.Notes.First(n => n.Pitch == 60);     // C4 应落在“低八度”(左键)
if (!c4.InRange || c4.OctaveSlot != Slot.Low || c4.Key != 'Z')
    throw new Exception("C4 映射错误");
var c8 = map.Notes.First(n => n.Pitch == 108);    // C8 超三个八度 → 空拍
if (c8.InRange) throw new Exception("C8 应被空拍跳过");
Console.WriteLine("映射断言 OK");

// ---------- 3) 全半音 12 键扫描 ----------
string[] expKey = { "Z", "Z", "X", "X", "C", "V", "V", "B", "B", "N", "N", "M" };
bool[] expSharp = { false, true, false, true, false, false, true, false, true, false, true, false };
for (int pc = 0; pc < 12; pc++)
{
    int p = 60 + pc;
    char k = NoteMapper.KeyOfPitch(p);
    bool s = NoteMapper.IsSharpPitch(p);
    if (k != expKey[pc][0] || s != expSharp[pc])
        throw new Exception($"pc={pc} 应为 {expKey[pc][0]}/sharp={expSharp[pc]}，实际 {k}/sharp={s}");
}
Console.WriteLine("全半音 12 键扫描 OK");

// ---------- 3.4) 和弦取根音测试 ----------
var chordSrc = new List<RawNote>
{
    new() { Pitch = 67, Start = 0, End = 1 },   // G（和弦中较高音）
    new() { Pitch = 60, Start = 0, End = 1 },   // C = 根音
    new() { Pitch = 64, Start = 0, End = 1 },   // E
    new() { Pitch = 62, Start = 1, End = 2 },   // 后面的单音 D
    new() { Pitch = 74, Start = 2, End = 3 },   // D5
};
var reduced = NoteMapper.ChordRootOnly(chordSrc);
if (reduced.Count != 3 || reduced[0].Pitch != 60 || reduced[1].Pitch != 62 || reduced[2].Pitch != 74)
    throw new Exception($"和弦取根失败: {string.Join(",", reduced.Select(x => x.Pitch))}");
Console.WriteLine("和弦取根音 OK（仅保留 60/62/74）");

// ---------- 3.5) 顶部高高音（右键+逗号 / +中键）映射测试 ----------
var topNotes = new[] { 72, 96, 97, 98, 84 }
    .Select((p, i) => new RawNote { Pitch = p, Start = i * 0.5, End = i * 0.5 + 0.4 })
    .ToList();
var tm = NoteMapper.Map(topNotes, 0, 5);   // 固定基准八度=5
var c7 = tm.Notes.First(x => x.Pitch == 96);   // C7 = 高高音do → 逗号+右键
if (!c7.InRange || c7.Key != ',' || c7.Sharp || c7.OctaveSlot != Slot.High)
    throw new Exception("高高音do 映射错误");
var cs7 = tm.Notes.First(x => x.Pitch == 97);  // C#7 = 高高音#do → 逗号+右键+中键
if (!cs7.InRange || cs7.Key != ',' || !cs7.Sharp || cs7.OctaveSlot != Slot.High)
    throw new Exception("高高音#do 映射错误");
var d7 = tm.Notes.First(x => x.Pitch == 98);   // D7 已超范围 → 空拍
if (d7.InRange) throw new Exception("D7 应空拍");
Console.WriteLine("高高音do/#do 映射 OK");

// ---------- 4) 外部传入的样例文件逐个解析 ----------
if (args.Length > 0)
{
    foreach (var f in args)
    {
        try
        {
            var p2 = MidiLoader.Parse(f);
            Console.WriteLine($"样例文件 OK: {f} → 候选{p2.Candidates.Count} 时长{p2.DurationSec:F2}s");
            foreach (var c in p2.Candidates)
            {
                var mc = NoteMapper.Map(c.Notes, 0, null);
                Console.WriteLine($"   轨{c.TrackIndex + 1} / 声道{c.Channel + 1}「{c.Name}」 音符{c.NoteCount} 音域{c.RangeLabel} → 可吹{mc.InRangeCount}/空拍{mc.SkipCount}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"样例文件 FAIL: {f} → {ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})");
        }
    }
}

Console.WriteLine("=== SMOKE TEST PASSED ===");

// ---------- 5) 引擎运行时自检（不真实按键；验证变速/跳转/结束不崩溃） ----------
Console.WriteLine("-- 引擎运行时自检 --");
var rnotes = new List<MappedNote>
{
    new() { Pitch = 60, Start = 0, End = 0.5, Key = 'Z', Sharp = false, OctaveSlot = Slot.Mid, InRange = true },
    new() { Pitch = 64, Start = 0.5, End = 1.0, Key = 'C', Sharp = false, OctaveSlot = Slot.Mid, InRange = true },
    new() { Pitch = 67, Start = 1.0, End = 1.5, Key = 'B', Sharp = false, OctaveSlot = Slot.Mid, InRange = true },
    new() { Pitch = 72, Start = 1.5, End = 2.5, Key = 'Z', Sharp = false, OctaveSlot = Slot.High, InRange = true },
    new() { Pitch = 74, Start = 2.5, End = 3.5, Key = 'X', Sharp = false, OctaveSlot = Slot.High, InRange = true },
};
var eng = new PlaybackEngine();
eng.Finished += () => Console.WriteLine("engine finished(自然结束)");
eng.Play(rnotes, 1.0, 25, false);
Thread.Sleep(350);                       // 播到 0.35s
eng.Speed = 2.0;                         // 实时提速
Thread.Sleep(250);                       // 音乐应走到 0.35+0.5=0.85
eng.SeekFraction(0.5);                   // 跳转到 50%
Thread.Sleep(600);                       // 2倍速应接近结尾/结束
double t1 = eng.ElapsedSeconds;
Console.WriteLine($"变速+跳转自检：elapsed={t1:F2}s total={eng.TotalSeconds:F1}s running={eng.IsRunning}");
if (!eng.IsRunning && t1 <= 0) throw new Exception("引擎异常结束");
eng.Stop();
Console.WriteLine("引擎运行时自检 OK");

// ---------- 6) 停止即时性自检 ----------
var eng2 = new PlaybackEngine();
var longNote = new List<MappedNote>
{
    new() { Pitch = 60, Start = 0, End = 60, Key = 'Z', Sharp = false, OctaveSlot = Slot.Mid, InRange = true },
    new() { Pitch = 72, Start = 30, End = 60, Key = 'Z', Sharp = false, OctaveSlot = Slot.High, InRange = true },
};
eng2.Play(longNote, 1.0, 25, false);
Thread.Sleep(120);
var stopWatch = System.Diagnostics.Stopwatch.StartNew();
eng2.Stop();
stopWatch.Stop();
if (eng2.IsRunning) throw new Exception("Stop 后引擎仍在运行");
Console.WriteLine($"停止即时性自检 OK（Stop 返回耗时 {stopWatch.ElapsedMilliseconds}ms）");
