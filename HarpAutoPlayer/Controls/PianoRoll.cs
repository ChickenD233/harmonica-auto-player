using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HarpAutoPlayer.Midi;

namespace HarpAutoPlayer;

/// <summary>
/// 可编辑的 MIDI 卷帘。横轴时间、纵轴音高。
/// 点空白=定位；点音符=选中；拖音符=移动；拖右缘=改长度；双击空白=加音；右键=删音。
/// </summary>
public sealed class PianoRoll : Control
{
    private const double PadX = 8, PadY = 6;
    private const double EdgeGrabPx = 6;        // 右侧改长度的抓取宽度
    private const double MinNoteSeconds = 0.03;
    private const int FallbackLo = 60, FallbackHi = 72;

    private IReadOnlyList<RawNote> _notes = Array.Empty<RawNote>();
    private HashSet<int> _inRange = new();
    private double _total;

    // 可见时间窗。整首歌挤进几百像素时一个音只有 1-2px，不缩放就没法编辑。
    private double _viewFrom, _viewTo;
    private bool _zoomed;                       // 用户缩放过就不再自动适配
    private double _position;
    private int _selected = -1;
    private int _hover = -1;

    // 缓存音符条几何：只有宽度/总长/音域/音符数变化时才重建
    private GeometryGroup? _barsOk, _barsSkip;
    private double _builtW = -1, _builtFrom = double.NaN, _builtTo = double.NaN;
    private int _builtLo = int.MinValue, _builtHi = int.MinValue, _builtCount = -1;

    private enum DragMode { None, Seek, Move, Resize, Pan }
    private DragMode _mode = DragMode.None;
    private int _dragIndex = -1;
    private double _grabOffset;                 // 按下点与音符起点的差（秒）
    private int _dragPitch;
    private double _dragStart, _dragEnd;
    private bool _dragged;
    private double _panStartX, _panStartFrom;

    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#F7F9FC"));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.Parse("#DDE3EA")), 1);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#E9EEF5")), 1);
    private static readonly IPen OctavePen = new Pen(new SolidColorBrush(Color.Parse("#D3DCE7")), 1);
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#2E9E5B"));
    private static readonly IBrush SkipBrush = new SolidColorBrush(Color.Parse("#C8D0D9"));
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.Parse("#8FB4E8")), 1);
    private static readonly IPen SelPen = new Pen(new SolidColorBrush(Color.Parse("#1F6FEB")), 2);
    private static readonly IPen HeadPen = new Pen(new SolidColorBrush(Color.Parse("#1F6FEB")), 2);
    private static readonly IBrush HeadBrush = new SolidColorBrush(Color.Parse("#1F6FEB"));
    private static readonly IBrush ReadoutBg = new SolidColorBrush(Color.Parse("#E8F0FC"));
    private static readonly IPen ReadoutBorder = new Pen(new SolidColorBrush(Color.Parse("#C3D6F2")), 1);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#2A3646"));

    /// <summary>拖动中持续触发：只挪指针，不打断播放。</summary>
    public event Action<double>? SeekPreview;
    /// <summary>松手：真正跳转。</summary>
    public event Action<double>? SeekCommitted;
    public event Action<int>? SelectionChanged;
    /// <summary>音符拖动结束：下标、新音高、新起点、新终点。</summary>
    public event Action<int, int, double, double>? NoteMoved;
    /// <summary>在空白处双击：要在此刻、此音高加一个音。</summary>
    public event Action<double, int>? NoteAddRequested;
    /// <summary>右键点中音符：要删掉它。</summary>
    public event Action<int>? NoteDeleteRequested;

    public bool SnapEnabled { get; set; } = true;
    public double SnapSeconds { get; set; } = 0.1;
    public int SelectedIndex => _selected;

    public PianoRoll()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetNotes(IReadOnlyList<RawNote> notes, IEnumerable<int> inRangePitches, double totalSeconds)
    {
        _notes = notes ?? Array.Empty<RawNote>();
        _inRange = new HashSet<int>(inRangePitches ?? Array.Empty<int>());
        _total = Math.Max(totalSeconds, 0.5);
        if (!_zoomed) { _viewFrom = 0; _viewTo = _total; }
        else ClampView();
        if (_selected >= _notes.Count) _selected = -1;
        _hover = -1;
        InvalidateVisual();
    }

    public void SetPosition(double seconds)
    {
        double v = Math.Clamp(seconds, 0, _total);
        if (Math.Abs(v - _position) < 0.0005) return;
        _position = v;
        InvalidateVisual();
    }

    /// <summary>显示全曲。载入新文件后调用。</summary>
    public void FitAll()
    {
        _zoomed = false;
        _viewFrom = 0;
        _viewTo = _total;
        InvalidateVisual();
    }

    /// <summary>以某时刻为锚点缩放。factor &lt; 1 放大。</summary>
    public void Zoom(double factor, double anchorSeconds)
    {
        double span = ViewSpan;
        double newSpan = Math.Clamp(span * factor, 0.35, Math.Max(_total, 0.5));
        double frac = (anchorSeconds - _viewFrom) / span;
        _viewFrom = anchorSeconds - frac * newSpan;
        _viewTo = _viewFrom + newSpan;
        _zoomed = true;
        ClampView();
        InvalidateVisual();
    }

    /// <summary>把视口约束在 [0, 总长] 内，并保证不小于 0.35 秒。</summary>
    private void ClampView()
    {
        double span = Math.Min(ViewSpan, Math.Max(_total, 0.5));
        if (_viewFrom < 0) _viewFrom = 0;
        if (_viewFrom + span > _total) _viewFrom = Math.Max(0, _total - span);
        _viewTo = _viewFrom + span;
    }

    public double ViewFrom => _viewFrom;
    public double ViewTo => _viewTo;

    /// <summary>整体平移视口（工具栏按钮用）。</summary>
    public void PanBy(double seconds)
    {
        double span = ViewSpan;
        _viewFrom = Math.Clamp(_viewFrom + seconds, 0, Math.Max(0, _total - span));
        _viewTo = _viewFrom + span;
        _zoomed = true;
        InvalidateVisual();
    }

    /// <summary>以视口中心为锚点缩放（工具栏按钮用）。</summary>
    public void ZoomCenter(double factor) => Zoom(factor, _viewFrom + ViewSpan / 2);

    public void Select(int index)
    {
        int v = index >= 0 && index < _notes.Count ? index : -1;
        if (v == _selected) return;
        _selected = v;
        SelectionChanged?.Invoke(v);
        InvalidateVisual();
    }

    // ================= 坐标换算（渲染与命中必须用同一套） =================

    private double PlotW => Math.Max(1, Bounds.Width - PadX * 2);
    private double PlotH => Math.Max(1, Bounds.Height - PadY * 2);

    private (int Lo, int Hi) PitchRange()
    {
        if (_notes.Count == 0) return (FallbackLo, FallbackHi);
        int lo = int.MaxValue, hi = int.MinValue;
        foreach (var n in _notes) { if (n.Pitch < lo) lo = n.Pitch; if (n.Pitch > hi) hi = n.Pitch; }
        if (hi - lo < 4) { int mid = (lo + hi) / 2; lo = mid - 2; hi = mid + 2; }   // 太窄不好点
        return (lo, hi);
    }

    private double RowH((int Lo, int Hi) r) => PlotH / Math.Max(1, r.Hi - r.Lo + 1);

    private double ViewSpan => Math.Max(1e-6, _viewTo - _viewFrom);

    private double XOf(double sec) => PadX + (sec - _viewFrom) / ViewSpan * PlotW;

    private double TimeAt(double x) => _viewFrom + Math.Clamp((x - PadX) / PlotW, 0, 1) * ViewSpan;

    private int PitchAt(double y, (int Lo, int Hi) r)
    {
        int p = r.Hi - (int)Math.Floor(Math.Clamp((y - PadY) / RowH(r), 0, r.Hi - r.Lo + 0.999));
        return Math.Clamp(p, 0, 127);
    }

    private Rect RectOf(int pitch, double start, double end)
    {
        var r = PitchRange();
        double rowH = RowH(r);
        double barH = Math.Max(3, rowH * 0.72);
        double x0 = XOf(start), x1 = XOf(end);
        double yc = r.Hi == r.Lo ? PadY + PlotH / 2 : PadY + (r.Hi - pitch) * rowH + rowH / 2;
        return new Rect(x0, yc - barH / 2, Math.Max(2.5, x1 - x0), barH);
    }

    private double Snap(double sec) =>
        !SnapEnabled || SnapSeconds <= 0 ? sec : Math.Round(sec / SnapSeconds) * SnapSeconds;

    /// <summary>
    /// 命中音符。先找完全落在矩形内的；音域宽时每行可能不到 2px，
    /// 这时退化为「横向必须在音符范围内，纵向取最近的一行」。
    /// </summary>
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
        return bestDy <= 12 ? best : -1;
    }

    // ================= 绘制 =================

    private void EnsureBars()
    {
        var (lo, hi) = PitchRange();
        if (_barsOk is not null && Math.Abs(_builtW - Bounds.Width) < 0.5
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
            if (n.End < _viewFrom || n.Start > _viewTo) continue;   // 视口外不画
            double x0 = XOf(n.Start), x1 = XOf(n.End);
            double yc = hi == lo ? PadY + PlotH / 2 : PadY + (hi - n.Pitch) * rowH + rowH / 2;
            var rect = new Rect(x0, yc - barH / 2, Math.Max(2.5, x1 - x0), barH);
            (_inRange.Contains(n.Pitch) ? ok : skip).Children.Add(new RectangleGeometry(rect));
        }
        _barsOk = ok;
        _barsSkip = skip;
        _builtW = Bounds.Width;
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
        if (w <= PadX * 2 + 8 || h <= PadY * 2 + 8 || _total <= 0) return;

        EnsureBars();

        double step = NiceStep(ViewSpan);
        double t0 = Math.Ceiling(_viewFrom / step) * step;
        for (double t = t0; t <= _viewTo; t += step)
        {
            double x = XOf(t);
            ctx.DrawLine(GridPen, new Point(x, PadY), new Point(x, PadY + PlotH));
        }

        var range = PitchRange();
        double rowH = RowH(range);
        for (int p = range.Lo; p <= range.Hi; p++)
        {
            if (p % 12 != 0) continue;      // 每个 C 画一条八度线
            double y = PadY + (range.Hi - p) * rowH;
            ctx.DrawLine(OctavePen, new Point(PadX, y), new Point(PadX + PlotW, y));
        }

        if (_barsSkip is not null) ctx.DrawGeometry(SkipBrush, null, _barsSkip);
        if (_barsOk is not null) ctx.DrawGeometry(OkBrush, null, _barsOk);

        // 拖动中：用底色擦掉原位置，再画临时位置
        bool dragging = _mode is DragMode.Move or DragMode.Resize && _dragIndex >= 0 && _dragIndex < _notes.Count;
        if (dragging)
        {
            var src = _notes[_dragIndex];
            ctx.FillRectangle(Bg, RectOf(src.Pitch, src.Start, src.End).Inflate(1.5));
            ctx.DrawRectangle(_inRange.Contains(_dragPitch) ? OkBrush : SkipBrush, SelPen,
                              RectOf(_dragPitch, _dragStart, _dragEnd));
        }
        else
        {
            if (_hover >= 0 && _hover < _notes.Count && _hover != _selected)
            {
                var n = _notes[_hover];
                ctx.DrawRectangle(null, HoverPen, RectOf(n.Pitch, n.Start, n.End).Inflate(1.5));
            }
            if (_selected >= 0 && _selected < _notes.Count)
            {
                var n = _notes[_selected];
                ctx.DrawRectangle(null, SelPen, RectOf(n.Pitch, n.Start, n.End).Inflate(1.5));
            }
        }

        double px = XOf(_position);
        ctx.DrawLine(HeadPen, new Point(px, PadY), new Point(px, PadY + PlotH));
        var head = new StreamGeometry();
        using (var c = head.Open())
        {
            c.BeginFigure(new Point(px - 4, PadY), true);
            c.LineTo(new Point(px + 4, PadY));
            c.LineTo(new Point(px, PadY + 6));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(HeadBrush, null, head);

        DrawReadout(ctx);
    }

    /// <summary>拖动或悬停时在左上角显示音名与时间，方便精确定位。</summary>
    private void DrawReadout(DrawingContext ctx)
    {
        bool editing = _mode is DragMode.Move or DragMode.Resize;
        int idx = editing ? _dragIndex : _hover;
        if (idx < 0 || idx >= _notes.Count) return;

        int pitch; double s, e;
        if (editing) { pitch = _dragPitch; s = _dragStart; e = _dragEnd; }
        else { var n = _notes[idx]; pitch = n.Pitch; s = n.Start; e = n.End; }

        string text = $"{Music.NoteName(pitch)}   {s:F2} - {e:F2}s";
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                   Typeface.Default, 12, TextBrush);
        var box = new Rect(PadX + 2, PadY + 2, ft.Width + 12, ft.Height + 6);
        ctx.DrawRectangle(ReadoutBg, ReadoutBorder, box, 4, 4);
        ctx.DrawText(ft, new Point(box.X + 6, box.Y + 3));
    }

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
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        int hit = HitTest(p);

        if (props.IsMiddleButtonPressed)
        {
            _mode = DragMode.Pan;
            _panStartX = p.X;
            _panStartFrom = _viewFrom;
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.SizeWestEast);
            e.Handled = true;
            return;
        }
        if (props.IsRightButtonPressed)
        {
            if (hit >= 0) { Select(hit); NoteDeleteRequested?.Invoke(hit); }
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed || _total <= 0) return;

        if (e.ClickCount >= 2 && hit < 0)
        {
            NoteAddRequested?.Invoke(Math.Max(0, Snap(TimeAt(p.X))), PitchAt(p.Y, PitchRange()));
            e.Handled = true;
            return;
        }

        e.Pointer.Capture(this);
        if (hit >= 0)
        {
            Select(hit);
            var n = _notes[hit];
            _dragIndex = hit;
            _dragPitch = n.Pitch;
            _dragStart = n.Start;
            _dragEnd = n.End;
            _dragged = false;
            var rect = RectOf(n.Pitch, n.Start, n.End);
            if (p.X >= rect.Right - EdgeGrabPx)
            {
                _mode = DragMode.Resize;
                Cursor = new Cursor(StandardCursorType.SizeWestEast);
            }
            else
            {
                _mode = DragMode.Move;
                _grabOffset = TimeAt(p.X) - n.Start;
                Cursor = new Cursor(StandardCursorType.SizeAll);
            }
        }
        else
        {
            Select(-1);
            _mode = DragMode.Seek;
            SeekPreview?.Invoke(TimeAt(p.X));
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
            int h = HitTest(p);
            if (h != _hover) { _hover = h; InvalidateVisual(); }
            return;
        }

        switch (_mode)
        {
            case DragMode.Seek:
                SeekPreview?.Invoke(TimeAt(p.X));
                break;

            case DragMode.Pan:
            {
                double span = ViewSpan;
                double dx = (p.X - _panStartX) / PlotW * span;
                _viewFrom = Math.Clamp(_panStartFrom - dx, 0, Math.Max(0, _total - span));
                _viewTo = _viewFrom + span;
                _zoomed = true;
                InvalidateVisual();
                break;
            }

            case DragMode.Move when _dragIndex >= 0 && _dragIndex < _notes.Count:
            {
                var n = _notes[_dragIndex];
                double len = Math.Max(MinNoteSeconds, n.End - n.Start);
                double ns = Math.Max(0, Snap(TimeAt(p.X) - _grabOffset));
                _dragStart = ns;
                _dragEnd = ns + len;
                _dragPitch = PitchAt(p.Y, PitchRange());
                _dragged = true;
                InvalidateVisual();
                break;
            }

            case DragMode.Resize when _dragIndex >= 0 && _dragIndex < _notes.Count:
            {
                var n = _notes[_dragIndex];
                _dragEnd = Math.Max(n.Start + MinNoteSeconds, Snap(TimeAt(p.X)));
                _dragged = true;
                InvalidateVisual();
                break;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        Cursor = new Cursor(StandardCursorType.Hand);

        var mode = _mode;
        _mode = DragMode.None;

        if (mode == DragMode.Seek)
        {
            SeekCommitted?.Invoke(TimeAt(e.GetPosition(this).X));
        }
        else if (_dragged && _dragIndex >= 0 && _dragIndex < _notes.Count)
        {
            NoteMoved?.Invoke(_dragIndex, _dragPitch, _dragStart, _dragEnd);
        }

        _dragIndex = -1;
        _dragged = false;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_total <= 0) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            PanBy(-Math.Sign(e.Delta.Y) * ViewSpan * 0.2);
        else
            Zoom(e.Delta.Y > 0 ? 0.8 : 1.25, TimeAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_mode == DragMode.None && _hover != -1) { _hover = -1; InvalidateVisual(); }
    }
}
