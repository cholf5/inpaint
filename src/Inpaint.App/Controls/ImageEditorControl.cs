using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Inpaint.App.Controls;

/// <summary>
/// 图片编辑画布：等比适配显示图片，半透明叠加遮罩层，
/// 左键拖动以图片分辨率把白色圆头笔触写入遮罩位图，并绘制画笔光标环。
/// </summary>
public class ImageEditorControl : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ImageEditorControl, IImage?>(nameof(Source));

    public static readonly StyledProperty<WriteableBitmap?> MaskProperty =
        AvaloniaProperty.Register<ImageEditorControl, WriteableBitmap?>(nameof(Mask));

    public static readonly StyledProperty<double> BrushSizeProperty =
        AvaloniaProperty.Register<ImageEditorControl, double>(nameof(BrushSize), 40.0);

    public static readonly StyledProperty<bool> IsPaintEnabledProperty =
        AvaloniaProperty.Register<ImageEditorControl, bool>(nameof(IsPaintEnabled), true);

    public static readonly StyledProperty<bool> ShowSizePreviewProperty =
        AvaloniaProperty.Register<ImageEditorControl, bool>(nameof(ShowSizePreview));

    /// <summary>左键在画布上落下新笔触后触发；ViewModel 借此感知遮罩已非空（Enter 修复快捷键的门槛）。</summary>
    public event EventHandler? StrokePainted;

    /// <summary>一笔涂抹结束（松开左键）后触发；「松手即修复」选项开启时据此自动执行修复。</summary>
    public event EventHandler? StrokeCommitted;

    static ImageEditorControl()
    {
        AffectsRender<ImageEditorControl>(SourceProperty);
        AffectsRender<ImageEditorControl>(MaskProperty);
        AffectsRender<ImageEditorControl>(BrushSizeProperty);
        AffectsRender<ImageEditorControl>(ShowSizePreviewProperty);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public WriteableBitmap? Mask
    {
        get => GetValue(MaskProperty);
        set => SetValue(MaskProperty, value);
    }

    /// <summary>画笔直径（屏幕像素，实际涂抹宽度随显示缩放换算到图片像素）。</summary>
    public double BrushSize
    {
        get => GetValue(BrushSizeProperty);
        set => SetValue(BrushSizeProperty, value);
    }

    public bool IsPaintEnabled
    {
        get => GetValue(IsPaintEnabledProperty);
        set => SetValue(IsPaintEnabledProperty, value);
    }

    /// <summary>调节画笔大小（拖动滑块或按 [ ] 快捷键）时，即使指针不在画布上也显示大小预览环。</summary>
    public bool ShowSizePreview
    {
        get => GetValue(ShowSizePreviewProperty);
        set => SetValue(ShowSizePreviewProperty, value);
    }

    private Rect _contentRect;
    private double _scale;
    private bool _stroking;
    private bool _hasLast;
    private Point _last;
    private Point _pointer;
    private bool _pointerInside;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height;
        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    public override void Render(DrawingContext context)
    {
        if (Source is not { } source)
        {
            _contentRect = default;
            return;
        }
        var size = source.Size;
        if (size.Width <= 0 || size.Height <= 0) return;

        double boundsW = Math.Max(1, Bounds.Width);
        double boundsH = Math.Max(1, Bounds.Height);
        double scale = Math.Min(boundsW / size.Width, boundsH / size.Height);
        var dest = new Rect(
            (boundsW - size.Width * scale) / 2,
            (boundsH - size.Height * scale) / 2,
            size.Width * scale,
            size.Height * scale);
        _contentRect = dest;
        _scale = scale;

        context.DrawImage(source, dest);
        if (Mask is { } mask)
        {
            using (context.PushOpacity(0.55))
                context.DrawImage(mask, dest);
        }

        if ((_pointerInside || ShowSizePreview) && IsPaintEnabled && BrushSize >= 4)
        {
            // 与实际涂抹的圆盘一致画圆形光标；指针不在画布上（如正在拖大小滑块）时以图片中心预览
            var center = _pointerInside
                ? _pointer
                : new Point(_contentRect.X + _contentRect.Width / 2, _contentRect.Y + _contentRect.Height / 2);
            double r = BrushSize / 2;
            var rect = new Rect(center.X - r, center.Y - r, BrushSize, BrushSize);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#00000099")), 1.5), rect);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#FFFFFFCC")), 1.0), rect.Deflate(1.5));
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        _pointerInside = true;
        InvalidateVisual();
        base.OnPointerEntered(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _pointerInside = false;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _pointer = e.GetPosition(this);
        if (_stroking && IsPaintEnabled)
        {
            if (ToImagePoint(_pointer) is { } p)
            {
                if (_hasLast) PaintSegment(_last, p);
                else PaintDisc(p);
                _last = p;
                _hasLast = true;
            }
        }
        InvalidateVisual();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!IsPaintEnabled || Source is null || Mask is null) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (ToImagePoint(e.GetPosition(this)) is not { } p) return;
        _stroking = true;
        _hasLast = false;
        e.Pointer.Capture(this);
        PaintDisc(p);
        StrokePainted?.Invoke(this, EventArgs.Empty);
        _last = p;
        _hasLast = true;
        InvalidateVisual();
        e.Handled = true;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_stroking)
        {
            _stroking = false;
            _hasLast = false;
            e.Pointer.Capture(null);
            // 只有左键开始过的笔触才会到这：右键/空闲抬起不触发
            StrokeCommitted?.Invoke(this, EventArgs.Empty);
        }
        base.OnPointerReleased(e);
    }

    /// <summary>控件坐标 → 图片像素坐标（钳制到图片范围内，笔画越界时贴边）。</summary>
    private Point? ToImagePoint(Point p)
    {
        if (_contentRect.Width <= 0 || _scale <= 0 || Source is not { } source) return null;
        var size = source.Size;
        return new Point(
            Math.Clamp((p.X - _contentRect.X) / _scale, 0, size.Width - 1),
            Math.Clamp((p.Y - _contentRect.Y) / _scale, 0, size.Height - 1));
    }

    private double BrushRadius() => Math.Max(1, BrushSize / 2 / Math.Max(0.01, _scale));

    private void PaintSegment(Point from, Point to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        double r = BrushRadius();
        int steps = Math.Max(1, (int)Math.Ceiling(dist / Math.Max(1, r / 2)));
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            PaintDisc(new Point(from.X + dx * t, from.Y + dy * t));
        }
    }

    /// <summary>以图片分辨率把白色实心圆写进遮罩位图（Bgra8888）。</summary>
    private void PaintDisc(Point center)
    {
        if (Mask is not { } mask) return;
        int w = mask.PixelSize.Width;
        int h = mask.PixelSize.Height;
        double r = BrushRadius();
        int x0 = Math.Max(0, (int)Math.Floor(center.X - r));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(center.X + r));
        int y0 = Math.Max(0, (int)Math.Floor(center.Y - r));
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(center.Y + r));
        if (x1 < x0 || y1 < y0) return;
        double r2 = r * r;
        double cx = center.X;
        double cy = center.Y;

        using var frame = mask.Lock();
        unsafe
        {
            var basePtr = (byte*)frame.Address;
            int stride = frame.RowBytes;
            for (int y = y0; y <= y1; y++)
            {
                double dy = y - cy;
                var row = basePtr + (long)stride * y;
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x - cx;
                    if (dx * dx + dy * dy > r2) continue;
                    var p = row + x * 4;
                    p[0] = 255;
                    p[1] = 255;
                    p[2] = 255;
                    p[3] = 255;
                }
            }
        }
    }
}
