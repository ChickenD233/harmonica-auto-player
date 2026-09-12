using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HarpAutoPlayer.Midi;

namespace HarpAutoPlayer;

/// <summary>
/// 可编辑的 MIDI 卷帘。上方时间标尺、左侧琴键栏、中间为音符网格。
///
/// 交互（对齐 signal / LMMS / Ardour 的通行做法）：
///   标尺            点或拖 = 定位
///   琴键栏          点 = 选中该音高的全部音符（按住拖动可连选多个音高）
///   拖音符中部      移动（多选时整组一起移动时间与音高）
///   拖音符任一端    改长度（左端改头、右端改尾，音高锁定）
///   空白处拖拽      框选；空白处单击 = 定位并清空选择
///   双击空白        加音；按住继续拖可一次把长度定好
///   右键            删音（右键拖动可连擦）
///   Delete          删除所选；Ctrl+A 全选；Esc 清空；方向键微调
///   滚轮            以光标为锚点缩放；Shift+滚轮平移；中键拖动平移
///   拖动时按 Shift  临时关闭吸附
///
/// 坐标换算一律走 XOf / TimeAt / YCenterOf / PitchAt（含标尺与琴键栏的沟槽偏移），
/// 渲染与命中共用同一套，避免"看到的位置"和"点中的位置"错位。
/// </summary>
public sealed class PianoRoll : Control
{
    // ---- 沟槽：标尺在上、琴键栏在左。换算里必须同时含这两个偏移，两个方向要对称 ----
    private const double KeyW = 50;          // 琴键栏宽
    private const double RulerH = 20;        // 标尺高
    private const double PadR = 8, PadB = 6; // 右/下留白
    private const double EdgeGrabPx = 6;     // 改长度的抓取带宽度（上限）
    private const double MinNoteSeconds = 0.03;
    private const double DragThreshold = 4;  // 超过它才算"拖"，否则算"点"
    private const int FallbackLo = 48, FallbackHi = 48 + MinSpan;   // C3 起，4 个八度
    /// <summary>可见音域的最小跨度（半音数）。4 个八度 = 48 个半音，即 hi-lo = 47。</summary>
    private const int MinSpan = 47;
    private int _lastLo = FallbackLo, _lastHi = FallbackHi;   // 上一次实际用过的音域

    private List<RawNote> _notes = new();
    private HashSet<int> _inRange = new();
    private double _total;
    private double _position;
    /// <summary>「去除开头空拍」剪掉的前奏秒数（0 = 未剪或该选项关闭）。</summary>
    private double _trimmedLead;

    // 画小节线用的速度/拍号（取不到时回落到 120bpm 4/4）
    private double _secPerBeat = 0.5;
    private int _beatsPerBar = 4;

    private double _viewFrom, _viewTo;
    private double _positionAtLastFollow = double.NaN;

    private readonly HashSet<int> _sel = new();
    private int _hover = -1;
    private int _hoverKey = -1;

    private GeometryGroup? _barsOk, _barsSkip;
    private double _builtW = -1, _builtH = -1, _builtFrom = double.NaN, _builtTo = double.NaN;
    private int _builtLo = int.MinValue, _builtHi = int.MinValue, _builtCount = -1;

    private enum DragMode { None, ScrubRuler, Pan, Marquee, Move, ResizeL, ResizeR, Erase, KeyLane }
    private DragMode _mode = DragMode.None;
    private bool _dragMoved;
    private Point _pressPt;
    private Rect _marquee;

    // 拖动基准：手势开始时记录一次，**之后只读**；每次指针移动都从这份基准重新算结果，
    // 绝不把上一帧的结果再叠加一次位移 —— 否则每帧都多走一段，音符会飞出去（实测"乱飘"）。
    private readonly List<(RawNote Note, int Pitch, double Start, double End)> _dragBase = new();
    // 本次移动算出来的临时位置，只用于绘制与提交
    private readonly List<(RawNote Note, int Pitch, double Start, double End)> _dragNow = new();
    private double _anchorSec;
    private int _anchorPitch;
    /// <summary>拖动中已经试听过的音高，防止同一音高反复发声。</summary>
    private int _lastAuditioned = int.MinValue;
    private readonly HashSet<int> _eraseDone = new();
    private readonly HashSet<int> _pitchSelDone = new();

    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#F7F9FC"));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.Parse("#DDE3EA")), 1);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#E9EEF5")), 1);
    private static readonly IPen BeatPen = new Pen(new SolidColorBrush(Color.Parse("#DCE4EE")), 1);
    private static readonly IPen BarPen = new Pen(new SolidColorBrush(Color.Parse("#C9D5E3")), 1);
    private static readonly IPen OctavePen = new Pen(new SolidColorBrush(Color.Parse("#E3E9F1")), 1);
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#2E9E5B"));
    private static readonly IBrush SkipBrush = new SolidColorBrush(Color.Parse("#C8D0D9"));
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.Parse("#8FB4E8")), 1);
    private static readonly IPen SelPen = new Pen(new SolidColorBrush(Color.Parse("#1F6FEB")), 2);
    private static readonly IPen HeadPen = new Pen(new SolidColorBrush(Color.Parse("#1F6FEB")), 2);
    private static readonly IBrush HeadBrush = new SolidColorBrush(Color.Parse("#1F6FEB"));
    private static readonly IBrush ReadoutBg = new SolidColorBrush(Color.Parse("#E8F0FC"));
    private static readonly IPen ReadoutBorder = new Pen(new SolidColorBrush(Color.Parse("#C3D6F2")), 1);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#2A3646"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#7C8798"));
    private static readonly IBrush KeyWhite = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush KeyBlack = new SolidColorBrush(Color.Parse("#E4EAF2"));
    private static readonly IBrush KeySel = new SolidColorBrush(Color.Parse("#C7DBFA"));
    private static readonly IBrush RulerBg = new SolidColorBrush(Color.Parse("#EEF3F9"));
    private static readonly IPen LaneBorder = new Pen(new SolidColorBrush(Color.Parse("#DDE3EA")), 1);
    private static readonly IBrush MarqueeFill = new SolidColorBrush(Color.Parse("#331F6FEB"));
    private static readonly IPen MarqueePen = new Pen(new SolidColorBrush(Color.Parse("#1F6FEB")), 1);

    private static readonly Typeface Face = Typeface.Default;
    private static readonly int[] BlackPc = { 1, 3, 6, 8, 10 };

    /// <summary>拖动中持续触发：只挪指针，不打断播放。</summary>
    public event Action<double>? SeekPreview;
    /// <summary>松手：真正跳转。</summary>
    public event Action<double>? SeekCommitted;
    /// <summary>选择集合变化。</summary>
    public event Action? SelectionChanged;
    /// <summary>一次编辑手势结束：提交新的完整谱面 + 可读的动作描述。</summary>
    public event Action<IReadOnlyList<RawNote>, string>? EditCommitted;
    /// <summary>视口 / 缩放 / 跟随状态变化，供工具栏刷新读数。</summary>
    public event Action? ViewChanged;
    /// <summary>试听请求：点选 / 新增 / 拖动改音高 / 点琴键栏时，把该音高交给上层发声。</summary>
    public event Action<int>? NoteAuditioned;

    public bool SnapEnabled { get; set; } = true;
    public double SnapSeconds { get; set; } = 0.1;
    /// <summary>加音用的默认长度，由 MainWindow 按当前谱面中位音长设置。</summary>
    public double DefaultNoteSeconds { get; set; } = 0.25;
    /// <summary>播放中自动把视口跟到播放头；用户手动滚动会关掉它。</summary>
    public bool FollowPlayhead { get; set; } = true;
    /// <summary>由 MainWindow 告知是否正在播放（只有播放中才自动跟随）。</summary>
    public bool IsPlaying { get; set; }

    public int SelectedCount => _sel.Count;
    /// <summary>卷帘当前持有的音符数。用于诊断"能出声但不显示"这类不同步问题。</summary>
    public int NoteCount => _notes.Count;
    public bool HasSelection => _sel.Count > 0;
    public double ViewFrom => _viewFrom;
    public double ViewTo => _viewTo;
    /// <summary>缩放读数：整首歌刚好铺满 = 100%。</summary>
    public double ZoomPercent => _total <= 0 ? 100 : _total / ViewSpan * 100.0;

    public PianoRoll()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = true;
        Focusable = true;
    }

    // ================= 数据 =================

    /// <summary>换谱：整首歌铺满视口并清空选择。载入文件 / 换轨 / 改取音选项后调用。</summary>
    public void SetNotes(IReadOnlyList<RawNote> notes, IEnumerable<int> inRangePitches, double totalSeconds,
                         bool preserveView = false)
    {
        _notes = new List<RawNote>(notes ?? Array.Empty<RawNote>());
        _inRange = new HashSet<int>(inRangePitches ?? Array.Empty<int>());
        _total = Math.Max(totalSeconds, 0.5);
        _sel.Clear();
        _hover = -1;
        if (preserveView) ClampView();      // 保留缩放与位置（撤销/重做时用），只把视口夹回新总长
        else { _viewFrom = 0; _viewTo = _total; }
        _positionAtLastFollow = double.NaN;
        InvalidateVisual();
        RaiseSelectionChanged();
        RaiseViewChanged();
    }

    /// <summary>
    /// 只更新「哪些音高可演奏」的集合（绿色 / 灰色由此决定）。
    /// 编辑改动音高后必须调用：SetNotes 只在换谱时走，改一个音的音高不会经过它，
    /// 否则颜色会按旧集合算 —— 明明在音域内的新音高会被画成灰色。
    /// </summary>
    public void SetInRangePitches(IEnumerable<int> pitches)
    {
        _inRange = new HashSet<int>(pitches ?? Array.Empty<int>());
        EnsureBarsInvalid();
        InvalidateVisual();
    }

    /// <summary>标尺画小节线用的速度与拍号。</summary>
    public void SetTempo(double secondsPerBeat, int beatsPerBar)
    {
        if (secondsPerBeat > 0.01 && secondsPerBeat < 10) _secPerBeat = secondsPerBeat;
        if (beatsPerBar >= 1 && beatsPerBar <= 16) _beatsPerBar = beatsPerBar;
        InvalidateVisual();
    }

    /// <summary>「去除开头空拍」剪掉的秒数；>0 时在卷帘左下角标出来。</summary>
    public void SetTrimInfo(double removedSeconds)
    {
        double v = Math.Max(0, removedSeconds);
        if (Math.Abs(v - _trimmedLead) < 1e-6) return;
        _trimmedLead = v;
        InvalidateVisual();
    }

    public void SetPosition(double seconds)
    {
        double v = Math.Clamp(seconds, 0, _total);
        if (Math.Abs(v - _position) > 0.0005)
        {
            _position = v;
            InvalidateVisual();
        }
        ApplyFollow();
    }

    // ================= 视口 =================

    public void FitAll()
    {
        _viewFrom = 0;
        _viewTo = _total;
        _positionAtLastFollow = double.NaN;
        InvalidateVisual();
        RaiseViewChanged();
    }

    /// <summary>以某时刻为锚点缩放。factor &lt; 1 放大。</summary>
    public void Zoom(double factor, double anchorSeconds)
    {
        if (_total <= 0) return;
        double span = ViewSpan;
        double newSpan = Math.Clamp(span * factor, 0.35, Math.Max(_total, 0.5));
        double frac = (anchorSeconds - _viewFrom) / span;
        _viewFrom = anchorSeconds - frac * newSpan;
        _viewTo = _viewFrom + newSpan;
        ClampView();
        InvalidateVisual();
        RaiseViewChanged();
    }

    public void ZoomCenter(double factor) => Zoom(factor, _viewFrom + ViewSpan / 2);

    /// <summary>整体平移视口。用户主动操作会关掉播放头跟随。</summary>
    public void PanBy(double seconds, bool userInitiated = true)
    {
        double span = ViewSpan;
        _viewFrom = Math.Clamp(_viewFrom + seconds, 0, Math.Max(0, _total - span));
        _viewTo = _viewFrom + span;
        if (userInitiated) SetFollow(false);
        InvalidateVisual();
        RaiseViewChanged();
    }

    public void SetFollow(bool on)
    {
        if (FollowPlayhead == on) return;
        FollowPlayhead = on;
        RaiseViewChanged();
    }

    private void ClampView()
    {
        double span = Math.Min(ViewSpan, Math.Max(_total, 0.5));
        if (_viewFrom < 0) _viewFrom = 0;
        if (_viewFrom + span > _total) _viewFrom = Math.Max(0, _total - span);
        _viewTo = _viewFrom + span;
    }

    /// <summary>播放头跑出视口时页跳跟随（播放头落到视口 30% 处）。</summary>
    private void ApplyFollow()
    {
        if (!FollowPlayhead || !IsPlaying || _total <= 0) return;
        double span = ViewSpan;
        if (span >= _total - 1e-9) return;                 // 全曲可见，不必跟随
        double rel = (_position - _viewFrom) / span;
        if (rel >= 0.02 && rel <= 0.7) return;             // 还在舒适区内
        if (Math.Abs(_positionAtLastFollow - _position) < 1e-6) return;
        _viewFrom = Math.Clamp(_position - span * 0.3, 0, Math.Max(0, _total - span));
        _viewTo = _viewFrom + span;
        _positionAtLastFollow = _position;
        InvalidateVisual();
        RaiseViewChanged();
    }

    // ================= 选择 =================

    public void ClearSelection()
    {
        if (_sel.Count == 0) return;
        _sel.Clear();
        InvalidateVisual();
        RaiseSelectionChanged();
    }

    public void SelectAll()
    {
        _sel.Clear();
        for (int i = 0; i < _notes.Count; i++) _sel.Add(i);
        InvalidateVisual();
        RaiseSelectionChanged();
    }

    /// <summary>删除所选（工具栏按钮 / Delete 键调用）。</summary>
    public void DeleteSelected()
    {
        if (_sel.Count == 0) return;
        int n = _sel.Count;
        var keep = new List<RawNote>(_notes.Count - n);
        for (int i = 0; i < _notes.Count; i++) if (!_sel.Contains(i)) keep.Add(_notes[i]);
        _notes = keep;
        _sel.Clear();
        Commit($"删除 {n} 个音");
    }

    // ================= 坐标换算（渲染与命中共用同一套） =================

    private double PlotW => Math.Max(1, Bounds.Width - KeyW - PadR);
    private double PlotH => Math.Max(1, Bounds.Height - RulerH - PadB);
    private double ViewSpan => Math.Max(1e-6, _viewTo - _viewFrom);

    private double XOf(double sec) => KeyW + (sec - _viewFrom) / ViewSpan * PlotW;

    private double TimeAt(double x) =>
        _viewFrom + Math.Clamp((x - KeyW) / PlotW, 0, 1) * ViewSpan;

    /// <summary>
    /// 纵向显示哪些音高。至少铺开一个八度：音域窄时行高才够点得中，
    /// 琴键栏也才像一排键。
    /// </summary>
    public static (int Lo, int Hi) ComputePitchRange(IReadOnlyList<RawNote> notes)
    {
        if (notes.Count == 0) return (FallbackLo, FallbackHi);
        int lo = int.MaxValue, hi = int.MinValue;
        foreach (var n in notes) { if (n.Pitch < lo) lo = n.Pitch; if (n.Pitch > hi) hi = n.Pitch; }

        // 最小可见音域 = 4 个八度。口琴本身能吹 3 个八度，如果画面只按谱面实际音高裁到
        // 一两个八度，用户就看不到可吹范围内的其它音高，也就没法把音符拖过去改。
        // 先按实际音高居中，再向两边扩到 MinSpan。
        int span = MinSpan;
        int center = (lo + hi) / 2;
        lo = center - span / 2;
        hi = lo + span;
        // 夹到合法 MIDI 音高
        if (lo < 0) { hi -= lo; lo = 0; }
        if (hi > 127) { lo -= hi - 127; hi = 127; }
        if (lo < 0) lo = 0;
        return (lo, hi);
    }

    /// <summary>
    /// 当前要画的音高范围。音符为空时不要退回固定的 C4–C5 —— 那会显示一个假的音域，
    /// 看起来像"音域突然只剩一个八度"。改为沿用上一次的范围。
    /// </summary>
    private (int Lo, int Hi) PitchRange()
    {
        if (_notes.Count == 0) return (_lastLo, _lastHi);
        var r = ComputePitchRange(_notes);
        _lastLo = r.Lo;
        _lastHi = r.Hi;
        return r;
    }

    private double RowH((int Lo, int Hi) r) => PlotH / Math.Max(1, r.Hi - r.Lo + 1);

    private double YCenterOf(int pitch, (int Lo, int Hi) r) =>
        RulerH + (r.Hi - pitch + 0.5) * RowH(r);

    private int PitchAt(double y, (int Lo, int Hi) r)
    {
        double row = (y - RulerH) / RowH(r);
        int p = r.Hi - (int)Math.Floor(Math.Clamp(row, 0, r.Hi - r.Lo + 0.999));
        return Math.Clamp(p, 0, 127);
    }

    private Rect RectOf(int pitch, double start, double end)
    {
        var r = PitchRange();
        double rowH = RowH(r);
        double barH = Math.Max(3, rowH * 0.72);
        double x0 = XOf(start), x1 = XOf(end);
        double yc = YCenterOf(pitch, r);
        return new Rect(x0, yc - barH / 2, Math.Max(2.5, x1 - x0), barH);
    }

    private static bool IsBlackKey(int pitch) => Array.IndexOf(BlackPc, Music.Mod(pitch, 12)) >= 0;

    private double Snap(double sec) =>
        !SnapEnabled || SnapSeconds <= 0 ? sec : Math.Round(sec / SnapSeconds) * SnapSeconds;

    private static bool ShiftHeld(KeyModifiers m) => m.HasFlag(KeyModifiers.Shift);

    /// <summary>一个吸附步长，同时作为改长度的下限。</summary>
    private double MinLen => SnapEnabled && SnapSeconds > 0 ? SnapSeconds : MinNoteSeconds;

    /// <summary>命中音符：先看矩形（外扩 3px）；行太薄时退化为"取纵向最近的一行"，但收紧到半行高。</summary>
    private int HitTest(Point p)
    {
        int best = -1;
        double bestDy = double.MaxValue;
        for (int i = _notes.Count - 1; i >= 0; i--)
        {
            var n = _notes[i];
            if (n.End < _viewFrom || n.Start > _viewTo) continue;
            var rect = RectOf(n.Pitch, n.Start, n.End);
            if (rect.Inflate(3).Contains(p)) return i;
            if (p.X >= rect.X - 4 && p.X <= rect.Right + 4)
            {
                double dy = Math.Abs(rect.Center.Y - p.Y);
                if (dy < bestDy) { bestDy = dy; best = i; }
            }
        }
        double limit = Math.Max(4, RowH(PitchRange()) * 0.55);
        return bestDy <= limit ? best : -1;
    }

    /// <summary>
    /// 改长度的抓取带宽度。固定 6px 时，任何窄于 6px 的音符都会整条落在抓取带里，
    /// 于是永远进不了"移动"分支 —— 短音符根本拖不动。按宽度比例收窄可避免。
    /// </summary>
    private static double GrabBand(double noteWidth) => Math.Min(EdgeGrabPx, Math.Max(2.0, noteWidth * 0.3));

    // ================= 绘制 =================

    private void EnsureBars()
    {
        var (lo, hi) = PitchRange();
        if (_barsOk is not null && Math.Abs(_builtW - Bounds.Width) < 0.5
            && Math.Abs(_builtH - Bounds.Height) < 0.5
            && Math.Abs(_builtFrom - _viewFrom) < 1e-9 && Math.Abs(_builtTo - _viewTo) < 1e-9
            && _builtLo == lo && _builtHi == hi && _builtCount == _notes.Count)
            return;

        double rowH = RowH((lo, hi));
        double barH = Math.Max(3, rowH * 0.72);
        var ok = new GeometryGroup();
        var skip = new GeometryGroup();
        foreach (var n in _notes)
        {
            if (n.End <= n.Start) continue;
            if (n.End < _viewFrom || n.Start > _viewTo) continue;   // 视口外不参与绘制
            double x0 = XOf(n.Start), x1 = XOf(n.End);
            double yc = YCenterOf(n.Pitch, (lo, hi));
            var rect = new Rect(x0, yc - barH / 2, Math.Max(2.5, x1 - x0), barH);
            (_inRange.Contains(n.Pitch) ? ok : skip).Children.Add(new RectangleGeometry(rect));
        }
        _barsOk = ok;
        _barsSkip = skip;
        _builtW = Bounds.Width;
        _builtH = Bounds.Height;
        _builtFrom = _viewFrom;
        _builtTo = _viewTo;
        _builtLo = lo;
        _builtHi = hi;
        _builtCount = _notes.Count;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        ctx.DrawRectangle(Bg, BorderPen, new Rect(0, 0, w, h), 6, 6);
        if (w <= KeyW + PadR + 8 || h <= RulerH + PadB + 8 || _total <= 0) return;

        EnsureBars();
        var range = PitchRange();

        DrawPlotGrid(ctx, range);
        DrawKeyLane(ctx, range);
        DrawNotes(ctx, range);
        DrawRuler(ctx);
        DrawMarquee(ctx);
        DrawPlayhead(ctx);
        DrawTrimNote(ctx);
        DrawReadout(ctx);
    }

    /// <summary>
    /// 「去除开头空拍」把整条旋律往前挪了，时间轴因此不是原始时间轴。
    /// 在左下角把剪掉多少标出来，否则用户无从知道这件事发生过。
    /// </summary>
    private void DrawTrimNote(DrawingContext ctx)
    {
        if (_trimmedLead <= 0.05) return;
        var ft = new FormattedText($"已剪掉开头 {_trimmedLead:F1}s 空拍",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5, MutedBrush);
        ctx.DrawText(ft, new Point(KeyW + 6, RulerH + PlotH - ft.Height - 2));
    }

    private void DrawPlotGrid(DrawingContext ctx, (int Lo, int Hi) range)
    {
        double top = RulerH, bottom = RulerH + PlotH;

        // 小节 / 拍线；太密就退回"秒"级网格，避免糊成一片
        double beatPx = _secPerBeat / ViewSpan * PlotW;
        if (beatPx >= 4)
        {
            double first = Math.Floor(_viewFrom / _secPerBeat) * _secPerBeat;
            for (double t = first; t <= _viewTo; t += _secPerBeat)
            {
                if (t < 0) continue;
                int beatIdx = (int)Math.Round(t / _secPerBeat);
                bool isBar = _beatsPerBar > 0 && beatIdx % _beatsPerBar == 0;
                double x = XOf(t);
                ctx.DrawLine(isBar ? BarPen : BeatPen, new Point(x, top), new Point(x, bottom));
            }
        }
        else
        {
            double step = NiceStep(ViewSpan);
            for (double t = Math.Ceiling(_viewFrom / step) * step; t <= _viewTo; t += step)
            {
                double x = XOf(t);
                ctx.DrawLine(GridPen, new Point(x, top), new Point(x, bottom));
            }
        }

        // 每个 C 一条八度线，作为纵向参照
        double rowH = RowH(range);
        for (int p = range.Lo; p <= range.Hi; p++)
        {
            if (Music.Mod(p, 12) != 0) continue;
            double y = RulerH + (range.Hi - p) * rowH;
            ctx.DrawLine(OctavePen, new Point(KeyW, y), new Point(KeyW + PlotW, y));
        }
    }

    /// <summary>左侧琴键栏：黑白键底色 + 音名标注，点击可选中该音高的全部音符。</summary>
    private void DrawKeyLane(DrawingContext ctx, (int Lo, int Hi) range)
    {
        double rowH = RowH(range);
        ctx.FillRectangle(RulerBg, new Rect(0, RulerH, KeyW - 1, PlotH));
        ctx.DrawLine(LaneBorder, new Point(KeyW, RulerH), new Point(KeyW, RulerH + PlotH));

        for (int p = range.Lo; p <= range.Hi; p++)
        {
            double y = RulerH + (range.Hi - p) * rowH;
            var rect = new Rect(0.5, y + 0.5, KeyW - 1.5, Math.Max(1, rowH - 1));
            var brush = _hoverKey == p ? KeySel : (IsBlackKey(p) ? KeyBlack : KeyWhite);
            ctx.FillRectangle(brush, rect);

            // 只标 C（各家的通行做法）：行高很薄时也只标 C 才不会糊成一片
            if (rowH < 7 || Music.Mod(p, 12) != 0) continue;
            var ft = new FormattedText(Music.NoteName(p), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face, 9, TextBrush);
            ctx.DrawText(ft, new Point(KeyW - 4 - ft.Width, y + (rowH - ft.Height) / 2));
        }
    }

    private void DrawNotes(DrawingContext ctx, (int Lo, int Hi) range)
    {
        // 灰条 = 超出音域（不可演奏），绿条 = 可演奏
        if (_barsSkip is not null) ctx.DrawGeometry(SkipBrush, null, _barsSkip);
        if (_barsOk is not null) ctx.DrawGeometry(OkBrush, null, _barsOk);

        // 拖动中：用底色擦掉原位，再画临时块（不重建几何缓存）
        if (_mode is DragMode.Move or DragMode.ResizeL or DragMode.ResizeR && _dragNow.Count > 0)
        {
            foreach (var o in _dragNow)
            {
                ctx.FillRectangle(Bg, RectOf(o.Note.Pitch, o.Note.Start, o.Note.End).Inflate(2));
                var r = RectOf(o.Pitch, o.Start, o.End);
                ctx.FillRectangle(_inRange.Contains(o.Pitch) ? OkBrush : SkipBrush, r);
                ctx.DrawRectangle(null, SelPen, r);
            }
            return;
        }

        if (_hover >= 0 && _hover < _notes.Count && !_sel.Contains(_hover))
        {
            var n = _notes[_hover];
            ctx.DrawRectangle(null, HoverPen, RectOf(n.Pitch, n.Start, n.End).Inflate(1.5));
        }
        foreach (int i in _sel)
        {
            if (i < 0 || i >= _notes.Count) continue;
            var n = _notes[i];
            ctx.DrawRectangle(null, SelPen, RectOf(n.Pitch, n.Start, n.End).Inflate(1.5));
        }
    }

    /// <summary>顶部标尺：小节号或秒数，便于判断"现在在第几小节"。</summary>
    private void DrawRuler(DrawingContext ctx)
    {
        ctx.FillRectangle(RulerBg, new Rect(0, 0, Bounds.Width, RulerH));
        ctx.DrawLine(LaneBorder, new Point(0, RulerH), new Point(Bounds.Width, RulerH));

        double barSec = _secPerBeat * _beatsPerBar;
        if (barSec > 0 && barSec / ViewSpan * PlotW >= 22)
        {
            int firstBar = (int)Math.Max(0, Math.Floor(_viewFrom / barSec));
            for (int k = firstBar; ; k++)
            {
                double t = k * barSec;
                if (t > _viewTo) break;
                double x = XOf(t);
                ctx.DrawLine(BarPen, new Point(x, RulerH - 7), new Point(x, RulerH));
                var ft = new FormattedText((k + 1).ToString(), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Face, 9.5, MutedBrush);
                ctx.DrawText(ft, new Point(x + 3, 1));
            }
        }
        else
        {
            double step = NiceStep(ViewSpan);
            for (double t = Math.Ceiling(_viewFrom / step) * step; t <= _viewTo; t += step)
            {
                double x = XOf(t);
                ctx.DrawLine(BarPen, new Point(x, RulerH - 6), new Point(x, RulerH));
                var ft = new FormattedText(FormatSeconds(t), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Face, 9.5, MutedBrush);
                ctx.DrawText(ft, new Point(x + 3, 1));
            }
        }
    }

    private void DrawMarquee(DrawingContext ctx)
    {
        if (_mode != DragMode.Marquee) return;
        if (_marquee.Width < 1 && _marquee.Height < 1) return;
        ctx.DrawRectangle(MarqueeFill, MarqueePen, _marquee);
    }

    private void DrawPlayhead(DrawingContext ctx)
    {
        double px = XOf(_position);
        if (px < KeyW - 1 || px > KeyW + PlotW + 1) return;
        ctx.DrawLine(HeadPen, new Point(px, RulerH), new Point(px, RulerH + PlotH));
        var head = new StreamGeometry();
        using (var c = head.Open())
        {
            c.BeginFigure(new Point(px - 4, RulerH), true);
            c.LineTo(new Point(px + 4, RulerH));
            c.LineTo(new Point(px, RulerH + 6));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(HeadBrush, null, head);
    }

    /// <summary>悬停 / 拖动时显示音名、时间与选中数量。</summary>
    private void DrawReadout(DrawingContext ctx)
    {
        string? text = null;
        if (_mode is DragMode.Move or DragMode.ResizeL or DragMode.ResizeR && _dragNow.Count > 0)
        {
            var o = _dragNow[0];
            string extra = _dragNow.Count > 1 ? $"   （{_dragNow.Count} 个）" : "";
            text = $"{Music.NoteName(o.Pitch)}   {o.Start:F2} - {o.End:F2}s{extra}";
        }
        else if (_hover >= 0 && _hover < _notes.Count)
        {
            var n = _notes[_hover];
            text = $"{Music.NoteName(n.Pitch)}   {n.Start:F2} - {n.End:F2}s";
        }
        if (text is null) return;

        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                   Face, 12, TextBrush);
        var box = new Rect(KeyW + 4, RulerH + 4, ft.Width + 12, ft.Height + 6);
        ctx.DrawRectangle(ReadoutBg, ReadoutBorder, box, 4, 4);
        ctx.DrawText(ft, new Point(box.X + 6, box.Y + 3));
    }

    private static string FormatSeconds(double t) =>
        t >= 60 ? $"{(int)(t / 60)}:{t % 60:00}" : $"{t:0.##}s";

    /// <summary>从 1/2/5/10… 里挑步长，让网格线落在 6-12 条。</summary>
    private static double NiceStep(double total)
    {
        double[] cand = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };
        foreach (var s in cand)
            if (total / s <= 12) return s;
        return total / 12.0;
    }

    // ================= 交互 =================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_total <= 0) return;

        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsMiddleButtonPressed)
        {
            _mode = DragMode.Pan;
            _pressPt = p;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeWestEast);
            e.Handled = true;
            return;
        }

        if (props.IsRightButtonPressed)
        {
            _mode = DragMode.Erase;
            _eraseDone.Clear();
            e.Pointer.Capture(this);
            EraseAt(p);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        // 标尺：定位
        if (p.Y < RulerH)
        {
            _mode = DragMode.ScrubRuler;
            _dragMoved = false;
            e.Pointer.Capture(this);
            SeekPreview?.Invoke(TimeAt(p.X));
            e.Handled = true;
            return;
        }

        // 琴键栏：选中该音高的全部音符（按住拖动连选）
        if (p.X < KeyW)
        {
            _mode = DragMode.KeyLane;
            _pitchSelDone.Clear();
            if (!ShiftHeld(e.KeyModifiers)) _sel.Clear();
            SelectPitchAt(p);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        int hit = HitTest(p);

        // 双击空白：加音，并立刻进入改尾手势，一次手势把位置与长度都定好
        if (e.ClickCount >= 2 && hit < 0)
        {
            double len = Math.Clamp(DefaultNoteSeconds, 0.05, 5.0);
            double st = Math.Max(0, Snap(TimeAt(p.X)));
            var n = new RawNote { Pitch = PitchAt(p.Y, PitchRange()), Start = st, End = st + len };
            _notes.Add(n);
            _notes.Sort((a, b) => a.Start.CompareTo(b.Start));
            int idx = _notes.IndexOf(n);
            _sel.Clear();
            _sel.Add(idx);
            BeginDrag(n, p);
            _mode = DragMode.ResizeR;
            e.Pointer.Capture(this);
            InvalidateVisual();
            RaiseSelectionChanged();
            NoteAuditioned?.Invoke(n.Pitch);
            e.Handled = true;
            return;
        }

        e.Pointer.Capture(this);
        _pressPt = p;
        _dragMoved = false;

        if (hit >= 0)
        {
            if (ShiftHeld(e.KeyModifiers))
            {
                if (!_sel.Remove(hit)) _sel.Add(hit);
                _mode = DragMode.None;
                InvalidateVisual();
                RaiseSelectionChanged();
                e.Handled = true;
                return;
            }

            // 已选中的音再按下时保留整组选择（便于整组拖动），否则只选它
            if (!_sel.Contains(hit)) { _sel.Clear(); _sel.Add(hit); }
            InvalidateVisual();
            RaiseSelectionChanged();

            var rect = RectOf(_notes[hit].Pitch, _notes[hit].Start, _notes[hit].End);
            double band = GrabBand(rect.Width);
            bool nearLeft = p.X <= rect.Left + band;
            bool nearRight = p.X >= rect.Right - band;

            BeginDrag(_notes[hit], p);
            NoteAuditioned?.Invoke(_notes[hit].Pitch);
            if (nearLeft && !nearRight) { _mode = DragMode.ResizeL; Cursor = new Cursor(StandardCursorType.SizeWestEast); }
            else if (nearRight) { _mode = DragMode.ResizeR; Cursor = new Cursor(StandardCursorType.SizeWestEast); }
            else { _mode = DragMode.Move; Cursor = new Cursor(StandardCursorType.SizeAll); }
        }
        else
        {
            // 空白：先"待定"，拖起来才是框选，松手没动就是定位
            _mode = DragMode.Marquee;
            _marquee = new Rect(p, p);
        }
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        if (_mode == DragMode.None)
        {
            UpdateHover(p);
            return;
        }

        switch (_mode)
        {
            case DragMode.ScrubRuler:
                SeekPreview?.Invoke(TimeAt(p.X));
                break;

            case DragMode.Pan:
            {
                double span = ViewSpan;
                double dx = (p.X - _pressPt.X) / PlotW * span;
                _viewFrom = Math.Clamp(_viewFrom - dx, 0, Math.Max(0, _total - span));
                _viewTo = _viewFrom + span;
                _pressPt = p;
                SetFollow(false);
                InvalidateVisual();
                break;
            }

            case DragMode.Marquee:
            {
                double dx = p.X - _pressPt.X, dy = p.Y - _pressPt.Y;
                if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < DragThreshold) break;
                _dragMoved = true;
                _marquee = new Rect(Math.Min(_pressPt.X, p.X), Math.Min(_pressPt.Y, p.Y),
                                    Math.Abs(dx), Math.Abs(dy));
                ApplyMarquee(_marquee);
                InvalidateVisual();
                break;
            }

            case DragMode.KeyLane:
                SelectPitchAt(p);
                break;

            case DragMode.Erase:
                EraseAt(p);
                break;

            case DragMode.Move:
                DoMoveDrag(p, e.KeyModifiers);
                break;

            case DragMode.ResizeL:
                DoResizeDrag(p, e.KeyModifiers, leftEdge: true);
                break;

            case DragMode.ResizeR:
                DoResizeDrag(p, e.KeyModifiers, leftEdge: false);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        Cursor = new Cursor(StandardCursorType.Hand);
        var mode = _mode;
        _mode = DragMode.None;

        switch (mode)
        {
            case DragMode.ScrubRuler:
                SeekCommitted?.Invoke(TimeAt(e.GetPosition(this).X));
                break;

            case DragMode.Marquee when !_dragMoved:
                ClearSelection();
                SeekCommitted?.Invoke(TimeAt(e.GetPosition(this).X));
                break;

            case DragMode.Move:
                if (_dragMoved) CommitDrag("移动");
                break;

            case DragMode.ResizeL:
            case DragMode.ResizeR:
                if (_dragMoved) CommitDrag("改长度");
                break;

            case DragMode.Erase:
                if (_eraseDone.Count > 0) Commit($"删除 {_eraseDone.Count} 个音");
                break;
        }

        _dragBase.Clear();
        _dragNow.Clear();
        _dragMoved = false;
        _marquee = default;
        if (mode == DragMode.KeyLane) RaiseSelectionChanged();
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_total <= 0) return;
        if (ShiftHeld(e.KeyModifiers))
            PanBy(-Math.Sign(e.Delta.Y) * ViewSpan * 0.2);
        else
            Zoom(e.Delta.Y > 0 ? 0.8 : 1.25, TimeAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_mode != DragMode.None) return;
        if (_hover != -1 || _hoverKey != -1)
        {
            _hover = -1;
            _hoverKey = -1;
            InvalidateVisual();
        }
    }

    /// <summary>Ctrl+A 全选、Esc 清空、方向键微调（左右按吸附步长、上下按半音）。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.A) { SelectAll(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { ClearSelection(); e.Handled = true; return; }
        if (_sel.Count == 0) return;

        double dt = 0;
        int dp = 0;
        switch (e.Key)
        {
            case Key.Left: dt = -MinLen * (shift ? 4 : 1); break;
            case Key.Right: dt = MinLen * (shift ? 4 : 1); break;
            case Key.Up: dp = shift ? 12 : 1; break;
            case Key.Down: dp = shift ? -12 : -1; break;
            default: return;
        }
        NudgeSelection(dt, dp);
        e.Handled = true;
    }

    // ================= 手势实现 =================

    /// <summary>记录手势基准：被抓音符 + 当前选择各自的原位。这份数据在手势期间不得修改。</summary>
    private void BeginDrag(RawNote grabbed, Point p)
    {
        _dragBase.Clear();
        _dragNow.Clear();
        _anchorSec = TimeAt(p.X);
        _anchorPitch = PitchAt(p.Y, PitchRange());
        _dragMoved = false;
        _lastAuditioned = int.MinValue;

        // 多选时整组一起动。以抓到的那个音为基准决定吸附与音高位移，
        // 其余音保持相对位置 —— 逐个吸附会把和弦/乐句拉散。
        var selRefs = new List<RawNote>();
        foreach (int i in _sel) if (i >= 0 && i < _notes.Count) selRefs.Add(_notes[i]);
        if (!selRefs.Contains(grabbed)) selRefs.Insert(0, grabbed);
        foreach (var n in selRefs) _dragBase.Add((n, n.Pitch, n.Start, n.End));
        int gi = _dragBase.FindIndex(o => ReferenceEquals(o.Note, grabbed));
        if (gi > 0)   // 把抓到的那个排到首位，作为吸附基准
        {
            var g = _dragBase[gi];
            _dragBase.RemoveAt(gi);
            _dragBase.Insert(0, g);
        }
        _dragNow.AddRange(_dragBase);
    }

    private void DoMoveDrag(Point p, KeyModifiers mods)
    {
        if (_dragBase.Count == 0) return;

        // 位移一律相对**基准**计算（TimeAt 内部会夹到视口内，所以拖出画布也不会失控）
        double d = TimeAt(p.X) - _anchorSec;
        int dp = PitchAt(p.Y, PitchRange()) - _anchorPitch;

        // 吸附"音符边"而不是光标：让基准音的头或尾落在格子上，取需要修正更小的那个
        var prim = _dragBase[0];
        if (SnapEnabled && SnapSeconds > 0)
        {
            double s = prim.Start + d, e = prim.End + d;
            double adjS = Snap(s) - s, adjE = Snap(e) - e;
            d += Math.Abs(adjS) <= Math.Abs(adjE) ? adjS : adjE;
        }

        // 整组移调时不允许任何一个音出界
        int minP = int.MaxValue, maxP = int.MinValue;
        foreach (var o in _dragBase) { if (o.Pitch < minP) minP = o.Pitch; if (o.Pitch > maxP) maxP = o.Pitch; }
        dp = Math.Clamp(dp, -minP, 127 - maxP);

        // 音高变了就试听一下，方便对准；同一个音高只响一次，避免拖动时狂响
        int leadPitch = prim.Pitch + dp;
        if (leadPitch != _lastAuditioned)
        {
            _lastAuditioned = leadPitch;
            NoteAuditioned?.Invoke(leadPitch);
        }

        _dragNow.Clear();
        foreach (var o in _dragBase)
        {
            double st = Math.Max(0, o.Start + d);
            _dragNow.Add((o.Note, o.Pitch + dp, st, Math.Max(st + MinNoteSeconds, o.End + d)));
        }
        _dragMoved = true;
        InvalidateVisual();
    }

    private void DoResizeDrag(Point p, KeyModifiers mods, bool leftEdge)
    {
        if (_dragBase.Count == 0) return;
        double t = TimeAt(p.X);
        if (SnapEnabled && SnapSeconds > 0) t = Snap(t);

        // 同样以基准为准：改左端时右端取基准的尾，改右端时左端取基准的头
        var b = _dragBase[0];
        double st = b.Start, en = b.End;
        if (leftEdge) st = Math.Min(en - MinLen, Math.Max(0, t));
        else en = Math.Max(st + MinLen, t);
        _dragNow.Clear();
        _dragNow.Add((b.Note, b.Pitch, st, en));
        _dragMoved = true;
        InvalidateVisual();
    }

    /// <summary>把拖动结果写回谱面：重建列表 + 排序 + 重映射选择，最后一次性提交。</summary>
    private void CommitDrag(string what)
    {
        if (_dragNow.Count == 0) return;

        var touched = new Dictionary<RawNote, (int Pitch, double Start, double End)>();
        foreach (var o in _dragNow) touched[o.Note] = (o.Pitch, o.Start, o.End);

        var selRefs = new HashSet<RawNote>();
        foreach (int i in _sel) if (i >= 0 && i < _notes.Count) selRefs.Add(_notes[i]);

        var rebuilt = new List<(RawNote N, bool Sel)>(_notes.Count);
        bool anyChange = false;
        foreach (var n in _notes)
        {
            if (touched.TryGetValue(n, out var v))
            {
                if (v.Pitch != n.Pitch || Math.Abs(v.Start - n.Start) > 1e-9 || Math.Abs(v.End - n.End) > 1e-9)
                    anyChange = true;
                rebuilt.Add((new RawNote { Pitch = v.Pitch, Start = v.Start, End = v.End, Velocity = n.Velocity }, true));
            }
            else
            {
                rebuilt.Add((n, selRefs.Contains(n)));
            }
        }
        if (!anyChange) return;

        rebuilt.Sort((a, b) => a.N.Start.CompareTo(b.N.Start));
        var next = new List<RawNote>(rebuilt.Count);
        var newSel = new HashSet<int>();
        for (int i = 0; i < rebuilt.Count; i++)
        {
            next.Add(rebuilt[i].N);
            if (rebuilt[i].Sel) newSel.Add(i);
        }
        _notes = next;
        _sel.Clear();
        foreach (int i in newSel) _sel.Add(i);

        Commit(_dragNow.Count > 1 ? $"{what} {_dragNow.Count} 个音" : what);
    }

    private void Commit(string what)
    {
        _hover = -1;
        EnsureBarsInvalid();
        InvalidateVisual();
        RaiseSelectionChanged();
        EditCommitted?.Invoke(new List<RawNote>(_notes), what);
    }

    /// <summary>音符集合变了，几何缓存必须失效（否则画面还是旧谱面）。</summary>
    private void EnsureBarsInvalid()
    {
        _barsOk = null;
        _barsSkip = null;
        _builtCount = -1;
    }

    private void NudgeSelection(double dt, int dp)
    {
        if (_sel.Count == 0) return;
        var selRefs = new HashSet<RawNote>();
        foreach (int i in _sel) if (i >= 0 && i < _notes.Count) selRefs.Add(_notes[i]);

        var rebuilt = new List<(RawNote N, bool Sel)>(_notes.Count);
        foreach (var n in _notes)
        {
            if (!selRefs.Contains(n)) { rebuilt.Add((n, false)); continue; }
            int p = Math.Clamp(n.Pitch + dp, 0, 127);
            double st = Math.Max(0, n.Start + dt);
            rebuilt.Add((new RawNote
            {
                Pitch = p,
                Start = st,
                End = Math.Max(st + MinNoteSeconds, n.End + dt),
                Velocity = n.Velocity
            }, true));
        }
        rebuilt.Sort((a, b) => a.N.Start.CompareTo(b.N.Start));
        var next = new List<RawNote>(rebuilt.Count);
        var newSel = new HashSet<int>();
        for (int i = 0; i < rebuilt.Count; i++)
        {
            next.Add(rebuilt[i].N);
            if (rebuilt[i].Sel) newSel.Add(i);
        }
        _notes = next;
        _sel.Clear();
        foreach (int i in newSel) _sel.Add(i);
        Commit("微调");
    }

    private void UpdateHover(Point p)
    {
        int h = p.X < KeyW ? -1 : HitTest(p);
        int hk = p.X < KeyW ? PitchAt(p.Y, PitchRange()) : -1;

        var want = StandardCursorType.Hand;
        if (h >= 0)
        {
            var rect = RectOf(_notes[h].Pitch, _notes[h].Start, _notes[h].End);
            double band = GrabBand(rect.Width);
            want = p.X <= rect.Left + band || p.X >= rect.Right - band
                ? StandardCursorType.SizeWestEast : StandardCursorType.SizeAll;
        }
        if (h != _hover || hk != _hoverKey)
        {
            _hover = h;
            _hoverKey = hk;
            InvalidateVisual();
        }
        Cursor = new Cursor(want);
    }

    private void ApplyMarquee(Rect r)
    {
        _sel.Clear();
        for (int i = 0; i < _notes.Count; i++)
        {
            var n = _notes[i];
            if (n.End < _viewFrom || n.Start > _viewTo) continue;
            if (RectOf(n.Pitch, n.Start, n.End).Intersects(r)) _sel.Add(i);
        }
        RaiseSelectionChanged();
    }

    private void SelectPitchAt(Point p)
    {
        int pitch = PitchAt(p.Y, PitchRange());
        if (!_pitchSelDone.Add(pitch)) return;
        for (int i = 0; i < _notes.Count; i++)
            if (_notes[i].Pitch == pitch) _sel.Add(i);
        InvalidateVisual();
        NoteAuditioned?.Invoke(pitch);
    }

    /// <summary>右键擦除：擦掉光标下的音。删除会移动下标，所以选择集要同步平移。</summary>
    private void EraseAt(Point p)
    {
        int hit = HitTest(p);
        if (hit < 0 || _eraseDone.Contains(hit)) return;
        _notes.RemoveAt(hit);
        _eraseDone.Add(hit);

        var shiftDown = new HashSet<int>();
        foreach (int i in _eraseDone) shiftDown.Add(i > hit ? i - 1 : i);
        _eraseDone.Clear();
        foreach (int i in shiftDown) _eraseDone.Add(i);

        var remapped = new HashSet<int>();
        foreach (int i in _sel)
        {
            if (i == hit) continue;
            remapped.Add(i > hit ? i - 1 : i);
        }
        _sel.Clear();
        foreach (int i in remapped) _sel.Add(i);

        EnsureBarsInvalid();
        InvalidateVisual();
    }

    private void RaiseSelectionChanged() => SelectionChanged?.Invoke();
    private void RaiseViewChanged() => ViewChanged?.Invoke();
}
