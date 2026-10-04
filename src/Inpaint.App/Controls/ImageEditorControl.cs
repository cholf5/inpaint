using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Inpaint.App.Controls;

/// <summary>
/// 图片编辑画布：默认等比适应窗口显示图片，半透明叠加遮罩层，
/// 左键拖动以图片分辨率把白色圆头笔触写入遮罩位图，并绘制画笔光标环。
/// 视图（缩放/平移）与图片数据完全分离，不进入生成历史：
/// ⌘/Ctrl/Alt+滚轮与触控板捏合以光标为锚点缩放，空格按住或中键拖拽平移；
/// 画笔为屏幕像素恒定语义（放大视图即提高涂抹精度），繁忙期间缩放平移仍可用。
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

    /// <summary>当前缩放标签（如 "100%"），控件内部维护，视图层直接绑定显示。</summary>
    public static readonly StyledProperty<string> ZoomLabelProperty =
        AvaloniaProperty.Register<ImageEditorControl, string>(nameof(ZoomLabel), "100%");

    /// <summary>左键在画布上落下新笔触后触发；ViewModel 借此感知遮罩已非空（Enter 修复快捷键的门槛）。</summary>
    public event EventHandler? StrokePainted;

    /// <summary>一笔涂抹结束（松开左键）后触发；「松手即修复」选项开启时据此自动执行修复。</summary>
    public event EventHandler? StrokeCommitted;

    /// <summary>滚轮在画布上滚动后触发；参数为本次滚轮纵向增量（正 = 放大画笔），MainWindow 据此调 ViewModel 的画笔大小。</summary>
    public event EventHandler<double>? BrushSizeWheel;

    /// <summary>缩放范围下限（5%，超大图适应后可能低于它，此时以适应值为准）。</summary>
    internal const double MinZoom = 0.05;

    /// <summary>缩放范围上限（3200%，与 PS 一致）。</summary>
    internal const double MaxZoom = 32.0;

    /// <summary>滚轮/捏合每档缩放因子。</summary>
    internal const double ZoomWheelStep = 1.2;

    /// <summary>快捷键与胶囊按钮每档缩放因子。</summary>
    internal const double ZoomKeyStep = 1.25;

    private static readonly Cursor PanCursor = new(StandardCursorType.Hand);

    static ImageEditorControl()
    {
        AffectsRender<ImageEditorControl>(SourceProperty);
        AffectsRender<ImageEditorControl>(MaskProperty);
        AffectsRender<ImageEditorControl>(BrushSizeProperty);
        AffectsRender<ImageEditorControl>(ShowSizePreviewProperty);
    }

    public ImageEditorControl()
    {
        // 触控板捏合缩放（macOS 双指开合）：无 On* 虚方法可重写，走路由事件；
        // 平台层逐事件给出增量 magnification（0.05 = 本次放大 5%），故因子 = 1 + Delta.X
        AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, (_, e) =>
        {
            if (Source is not null && e.Delta.X != 0 && !_stroking && !_panning)
                ZoomAt(e.GetPosition(this), 1 + e.Delta.X);
        });
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

    public string ZoomLabel
    {
        get => GetValue(ZoomLabelProperty);
        set => SetValue(ZoomLabelProperty, value);
    }

    // ---- 视图状态（纯显示，不落盘、不进历史）----

    private Rect _contentRect;
    private double _zoom = 1;       // 绝对缩放：1 图片像素 = _zoom DIP（1 = 100%）
    private Point _pan;             // 图片左上角在控件坐标系中的位置
    private bool _fitMode = true;   // 适应窗口模式：跟随窗口尺寸重算缩放；显式缩放后退出
    private Size _lastSourceSize;
    private bool _stroking;
    private bool _hasLast;
    private Point _last;
    private Point _pointer;
    private bool _pointerInside;
    private bool _panMode;          // 空格按住的临时平移模式
    private bool _panning;          // 平移拖拽进行中
    private Point _panStart;
    private Point _panOrigin;

    /// <summary>当前绝对缩放（1 = 100%）。internal 供单测。</summary>
    internal double Zoom => _zoom;

    /// <summary>图片左上角在控件坐标系中的偏移。internal 供单测。</summary>
    internal Point Pan => _pan;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height;
        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    public override void Render(DrawingContext context)
    {
        _rendering = true;
        try
        {
            UpdateView();
            if (Source is not { } source || _contentRect.Width <= 0) return;

            context.DrawImage(source, _contentRect);
            if (Mask is { } mask)
            {
                using (context.PushOpacity(0.55))
                    context.DrawImage(mask, _contentRect);
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
        finally
        {
            _rendering = false;
        }
    }

    // ---- 视图缩放/平移 ----

    /// <summary>以视口中心为锚点步进放大/缩小。</summary>
    internal void ZoomIn() => ZoomTo(_zoom * ZoomKeyStep);

    internal void ZoomOut() => ZoomTo(_zoom / ZoomKeyStep);

    /// <summary>回到 100% 实际大小（锚视口中心）。</summary>
    internal void SetActualSize() => ZoomTo(1.0);

    /// <summary>以 anchor 为锚点缩放 factor 倍：保持锚点下的图像点在屏幕上不动。</summary>
    internal void ZoomAt(Point anchor, double factor) => ZoomTo(_zoom * factor, anchor);

    /// <summary>缩放到指定绝对倍率（1 = 100%），anchor 为保持不动的视口点（缺省视口中心）。</summary>
    internal void ZoomTo(double zoom, Point? anchor = null)
    {
        if (Source is not { } source) return;
        var bounds = Viewport();
        // 常规钳制到 [5%, 3200%]；适应值落在范围外时（极小图/极大图）以当前值为界，避免缩放方向被钳反
        double lo = Math.Min(MinZoom, _zoom);
        double hi = Math.Max(MaxZoom, _zoom);
        double newZoom = Math.Clamp(zoom, lo, hi);
        var a = anchor ?? new Point(bounds.Width / 2, bounds.Height / 2);
        if (newZoom != _zoom)
        {
            _pan = ZoomPanAt(_pan, _zoom, newZoom, a, source.Size, bounds);
            _zoom = newZoom;
        }
        _fitMode = false;
        UpdateView();
        InvalidateVisual();
    }

    /// <summary>回到适应窗口：跟随窗口尺寸自动重算缩放并居中。</summary>
    internal void FitToWindow()
    {
        _fitMode = true;
        UpdateView();
        InvalidateVisual();
    }

    /// <summary>空格按住的临时平移模式（MainWindow 键盘事件转接）；仅影响光标与左键按下时的行为。</summary>
    internal void SetPanMode(bool on)
    {
        if (_panMode == on) return;
        _panMode = on;
        UpdateCursor();
    }

    /// <summary>触控板捏合入口，scale 为本次事件的乘性因子（1.2 = 放大 20%）。internal 供单测。</summary>
    internal void HandleMagnify(Point anchor, double scale) => ZoomAt(anchor, scale);

    /// <summary>
    /// 根据当前视图状态重算显示矩形：适应模式重算缩放并居中，否则钳制平移范围。
    /// Render 每帧调用（处理窗口尺寸变化与新图），缩放平移路径也调用以保证事件时坐标即时可用。
    /// </summary>
    private void UpdateView()
    {
        if (Source is not { } source)
        {
            _contentRect = default;
            return;
        }
        var size = source.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            _contentRect = default;
            return;
        }
        var bounds = Viewport();
        if (size != _lastSourceSize)
        {
            _lastSourceSize = size;
            _fitMode = true; // 打开新图或超分等尺寸变化 → 重新适应窗口
        }
        if (_fitMode)
            _zoom = FitScale(size, bounds);
        _pan = _fitMode
            ? CenterPan(size, _zoom, bounds)
            : ClampPan(_pan, _zoom, size, bounds);
        _contentRect = new Rect(_pan, new Size(size.Width * _zoom, size.Height * _zoom));
        UpdateZoomLabel();
        UpdateInterpolation();
    }

    private Size Viewport() => new(Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height));

    private static double FitScale(Size size, Size bounds) =>
        Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);

    private static Point CenterPan(Size size, double zoom, Size bounds) =>
        ClampPan(
            new Point((bounds.Width - size.Width * zoom) / 2, (bounds.Height - size.Height * zoom) / 2),
            zoom, size, bounds);

    /// <summary>平移钳制：图片小于视口的轴居中（不让留白被拖走），大于视口的轴贴边不越界。</summary>
    internal static Point ClampPan(Point pan, double zoom, Size size, Size bounds)
    {
        double w = size.Width * zoom;
        double h = size.Height * zoom;
        double x = w <= bounds.Width ? (bounds.Width - w) / 2 : Math.Clamp(pan.X, bounds.Width - w, 0);
        double y = h <= bounds.Height ? (bounds.Height - h) / 2 : Math.Clamp(pan.Y, bounds.Height - h, 0);
        return new Point(x, y);
    }

    /// <summary>锚点缩放平移换算：图像点 (anchor-pan)/oldZoom 在新倍率下仍落在 anchor 上，再钳制。internal 供单测。</summary>
    internal static Point ZoomPanAt(Point pan, double oldZoom, double newZoom, Point anchor, Size size, Size bounds)
    {
        double imgX = (anchor.X - pan.X) / oldZoom;
        double imgY = (anchor.Y - pan.Y) / oldZoom;
        return ClampPan(new Point(anchor.X - imgX * newZoom, anchor.Y - imgY * newZoom), newZoom, size, bounds);
    }

    /// <summary>缩放标签在渲染 pass 内同样不能联动绑定目标（TextBlock 失效），推迟到下一帧。</summary>
    private void UpdateZoomLabel()
    {
        var pct = (_zoom * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        if (pct == ZoomLabel) return;
        if (_rendering)
            Dispatcher.UIThread.Post(() => SetCurrentValue(ZoomLabelProperty, pct), DispatcherPriority.Background);
        else
            SetCurrentValue(ZoomLabelProperty, pct);
    }

    // 哨兵初值：与两个真实档位都不同，保证首次 UpdateView 必应用一次
    private BitmapInterpolationMode _interpolation = BitmapInterpolationMode.LowQuality;
    private bool _rendering;

    /// <summary>≥100% 用最近邻看硬像素便于精确对位，&lt;100% 用高质量插值避免缩小闪烁。
    /// 渲染 pass 内不允许任何触发 InvalidateVisual 的变更（RenderOptions 赋值会失效自身），推迟到下一帧。</summary>
    private void UpdateInterpolation()
    {
        var mode = _zoom >= 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        if (mode == _interpolation) return;
        _interpolation = mode;
        ApplyInterpolation(mode);
    }

    private void ApplyInterpolation(BitmapInterpolationMode mode)
    {
        if (_rendering)
            Dispatcher.UIThread.Post(() => RenderOptions.SetBitmapInterpolationMode(this, mode),
                DispatcherPriority.Background);
        else
            RenderOptions.SetBitmapInterpolationMode(this, mode);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        _pointerInside = true;
        UpdateCursor();
        InvalidateVisual();
        base.OnPointerEntered(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _pointerInside = false;
        UpdateCursor();
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        _pointer = e.GetPosition(this);
        if (_panning)
        {
            _pan = new Point(_panOrigin.X + _pointer.X - _panStart.X, _panOrigin.Y + _pointer.Y - _panStart.Y);
            UpdateView();
            InvalidateVisual();
            base.OnPointerMoved(e);
            return;
        }
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
        if (_panning)
        {
            e.Handled = true;
            return;
        }
        var point = e.GetCurrentPoint(this);
        var position = e.GetPosition(this);
        // 中键拖拽 / 空格按住时左键拖拽 = 平移画布（不落笔），繁忙期间同样可用
        if (Source is not null &&
            (point.Properties.IsMiddleButtonPressed || (point.Properties.IsLeftButtonPressed && _panMode)))
        {
            _panning = true;
            _stroking = false;
            _panStart = position;
            _panOrigin = _pan;
            e.Pointer.Capture(this);
            UpdateCursor();
            e.Handled = true;
            base.OnPointerPressed(e);
            return;
        }

        if (!IsPaintEnabled || Source is null || Mask is null) return;
        if (!point.Properties.IsLeftButtonPressed) return;
        // 落笔须在图片显示区内：缩放后图片只占视口一角时，留白区域不落笔（拖拽中途越界仍贴边）
        if (!_contentRect.Contains(position)) return;
        if (ToImagePoint(position) is not { } p) return;
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
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            UpdateCursor();
            InvalidateVisual();
            base.OnPointerReleased(e);
            return;
        }
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

    /// <summary>
    /// 滚轮：⌘/Ctrl/Alt+滚轮以光标为锚点缩放（繁忙期间同样可用）；
    /// 无修饰键调画笔大小——涂抹进行中不响应（半径突变会切断笔画），画笔不可用则放行事件。
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        bool zoomModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta)
            || e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (zoomModifier)
        {
            if (Source is not null && e.Delta.Y != 0 && !_stroking && !_panning)
            {
                ZoomAt(e.GetPosition(this), Math.Pow(ZoomWheelStep, e.Delta.Y));
                e.Handled = true;
            }
        }
        else if (!_stroking && IsPaintEnabled && Source is not null && e.Delta.Y != 0)
        {
            BrushSizeWheel?.Invoke(this, e.Delta.Y);
            e.Handled = true;
        }
        base.OnPointerWheelChanged(e);
    }

    private void UpdateCursor() =>
        Cursor = (_panMode || _panning) && _pointerInside ? PanCursor : null;

    /// <summary>控件坐标 → 图片像素坐标（钳制到图片范围内，笔画越界时贴边）。</summary>
    private Point? ToImagePoint(Point p)
    {
        if (_contentRect.Width <= 0 || _zoom <= 0 || Source is not { } source) return null;
        var size = source.Size;
        return new Point(
            Math.Clamp((p.X - _contentRect.X) / _zoom, 0, size.Width - 1),
            Math.Clamp((p.Y - _contentRect.Y) / _zoom, 0, size.Height - 1));
    }

    private double BrushRadius() => Math.Max(1, BrushSize / 2 / Math.Max(0.01, _zoom));

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
