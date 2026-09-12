using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HarpAutoPlayer.Engine;

namespace HarpAutoPlayer;

/// <summary>MIDI 卷帘：横轴时间，点或拖动定位。绿条=可演奏，灰条=超音域。</summary>
public sealed class PianoRoll : Control
{
    private const double PadX = 6, PadY = 4;

    private IReadOnlyList<MappedNote> _notes = Array.Empty<MappedNote>();
    private double _total;
    private double _position;
    private Geometry? _barsOk;      // 缓存几何：Render 每帧只画它，不遍历音符
    private Geometry? _barsSkip;
    private bool _dragging;

    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#F7F9FC"));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.Parse("#DDE3EA")), 1);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#E6EBF1")), 1);
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#2E9E5B"));
    private static readonly IBrush SkipBrush = new SolidColorBrush(Color.Parse("#C8D0D9"));
    private static readonly IPen HeadPen = new Pen(new SolidColorBrush(Color.Parse("#1F6FEB")), 2);
    private static readonly IBrush HeadBrush = new SolidColorBrush(Color.Parse("#1F6FEB"));

    /// <summary>拖动中持续触发，用于只挪指针（不打断播放）。</summary>
    public event Action<double>? SeekPreview;

    /// <summary>松手时触发一次，用于真正跳转。</summary>
    public event Action<double>? SeekCommitted;

    public PianoRoll()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = true;
    }

    /// <summary>换谱。空列表或 total&lt;=0 时只画底板。</summary>
    public void SetNotes(IReadOnlyList<MappedNote> notes, double totalSeconds)
    {
        _notes = notes ?? Array.Empty<MappedNote>();
        _total = totalSeconds;
        _position = Math.Clamp(_position, 0, Math.Max(0, _total));
        RebuildBars();
        InvalidateVisual();
    }

    public void SetPosition(double seconds)
    {
        double v = Math.Clamp(seconds, 0, Math.Max(0, _total));
        if (Math.Abs(v - _position) < 0.0005) return;   // 位置没变就别重画
        _position = v;
        InvalidateVisual();
    }

    /// <summary>尺寸变化必须重建几何，否则音符条按旧宽度摆放。</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = base.ArrangeOverride(finalSize);
        RebuildBars();
        return s;
    }

    private double PlotW => Math.Max(1, Bounds.Width - PadX * 2);
    private double PlotH => Math.Max(1, Bounds.Height - PadY * 2);

    private void RebuildBars()
    {
        if (_notes.Count == 0 || _total <= 0 || Bounds.Width <= 0)
        {
            _barsOk = _barsSkip = null;
            return;
        }

        int lo = _notes.Min(n => n.Pitch);
        int hi = _notes.Max(n => n.Pitch);
        double rowH = PlotH / Math.Max(1, hi - lo + 1);
        double barH = Math.Max(2, rowH * 0.72);

        var ok = new GeometryGroup();
        var skip = new GeometryGroup();
        foreach (var n in _notes)
        {
            if (n.End <= n.Start) continue;
            double x0 = PadX + n.Start / _total * PlotW;
            double x1 = PadX + n.End / _total * PlotW;
            double w = Math.Max(1.5, x1 - x0);   // 极短的音也要看得见
            double yc = hi == lo
                ? PadY + PlotH / 2
                : PadY + (hi - n.Pitch) * rowH + rowH / 2;
            (n.InRange ? ok : skip).Children.Add(new RectangleGeometry(new Rect(x0, yc - barH / 2, w, barH)));
        }
        _barsOk = ok;
        _barsSkip = skip;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Bg, BorderPen, new Rect(0, 0, Bounds.Width, Bounds.Height), 6, 6);
        if (_total <= 0 || _notes.Count == 0) return;

        double step = NiceStep(_total);
        for (double t = step; t < _total; t += step)
        {
            double x = PadX + t / _total * PlotW;
            ctx.DrawLine(GridPen, new Point(x, PadY), new Point(x, PadY + PlotH));
        }

        if (_barsSkip is not null) ctx.DrawGeometry(SkipBrush, null, _barsSkip);
        if (_barsOk is not null) ctx.DrawGeometry(OkBrush, null, _barsOk);

        double px = PadX + Math.Clamp(_position, 0, _total) / _total * PlotW;
        ctx.DrawLine(HeadPen, new Point(px, PadY), new Point(px, PadY + PlotH));

        var head = new StreamGeometry();     // 顶部小三角，位置更醒目
        using (var c = head.Open())
        {
            c.BeginFigure(new Point(px - 4, PadY), true);
            c.LineTo(new Point(px + 4, PadY));
            c.LineTo(new Point(px, PadY + 6));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(HeadBrush, null, head);
    }

    /// <summary>从 1/2/5/10… 里挑步长，使网格线落在 6–10 条。</summary>
    private static double NiceStep(double total)
    {
        double[] cand = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };
        foreach (var s in cand)
            if (total / s <= 10) return s;
        return total / 10.0;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        Emit(e.GetPosition(this).X, committed: false);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Emit(e.GetPosition(this).X, committed: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
        Emit(e.GetPosition(this).X, committed: true);
    }

    /// <summary>x 换算成秒：映射必须与渲染一致（含左右内边距）。</summary>
    private void Emit(double x, bool committed)
    {
        if (_total <= 0) return;
        double frac = Math.Clamp((x - PadX) / PlotW, 0, 1);
        double sec = frac * _total;
        if (committed) SeekCommitted?.Invoke(sec);
        else SeekPreview?.Invoke(sec);
    }
}
