using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Inpaint.App.Services;
using Inpaint.App.ViewModels;

namespace Inpaint.App;

public partial class ExportWindow : Window
{
    /// <summary>预览视口尺寸（1:1 像素取样窗，与 XAML 中 Border 的 Width/Height 一致）。internal 供单测。</summary>
    internal static readonly Size PreviewViewportSize = new(300, 200);

    /// <summary>全图导航器尺寸（与 XAML 中 Border 的 Width/Height 一致）。internal 供单测。</summary>
    internal static readonly Size ThumbViewportSize = new(300, 120);

    private readonly TranslateTransform _cropTranslate = new();
    private bool _showingOriginal;
    private bool _thumbDragging;
    /// <summary>取样中心（图片像素坐标）；null = 待窗口打开时初始化为图片中心。</summary>
    private Point? _cropCenter;
    private Point _lastPointer;
    /// <summary>导航器缩略图的等比缩放与内容区偏移（Stretch=Uniform 的信箱布局），由打开时计算。</summary>
    private double _thumbScale = 1;
    private Point _thumbOffset;

    public ExportWindow()
    {
        InitializeComponent();
        PreviewImage.RenderTransform = _cropTranslate;
        // 不能用 ClipToBounds：Avalonia 的 ClipToBounds 裁剪发生在子项自身坐标系、会跟着 RenderTransform
        // 一起移动，负平移会把可见区整个移出视口；显式 Clip 在 Border 坐标系裁剪，才能当取样窗用
        PreviewViewport.Clip = new RectangleGeometry(new Rect(PreviewViewportSize));
        Closed += (_, _) =>
        {
            if (DataContext is ExportViewModel viewModel)
            {
                viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                viewModel.Detach();
            }
        };
    }

    /// <summary>模态导出对话框：source 归历史树所有（模态期间不会被释放），对话框只读；initial 为上次导出设置。</summary>
    public ExportWindow(Bitmap source, ExportOptions? initial) : this()
    {
        var viewModel = new ExportViewModel(
            source,
            initial?.Format ?? ExportFormat.Png,
            initial?.Quality ?? ImageExporter.DefaultQuality);
        // 确认后把（复用预估或现编码的）字节作为对话框结果带回，null = 用户取消
        viewModel.ExportConfirmed += choice => Close(choice);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = viewModel;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 导航器缩略图用原图渲染，打开即有内容（不等预估）；首轮预估也等窗口真正显示后才开始
        UpdateThumbnail();
        (DataContext as ExportViewModel)?.StartInitialEstimate();
        UpdatePreview();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportViewModel.PreviewResult)) UpdatePreview();
    }

    // ---- 1:1 取样预览：按住 = 看原图，拖动 = 移动取样位置 ----

    private void UpdatePreview()
    {
        if (DataContext is not ExportViewModel viewModel) return;
        PreviewImage.Source = _showingOriginal ? viewModel.PreviewOriginal : viewModel.PreviewResult;
        PreviewPlaceholder.IsVisible = PreviewImage.Source is null;
        if (PreviewImage.Source is Bitmap bitmap)
        {
            // 两件事都在这：① Image 控件自身会裁掉超出 Bounds 的绘制，须铺满整张位图，取样窗交给 Border.Clip；
            // ② Stretch.Fill + 显式像素尺寸强制 1 图像像素 = 1 DIP 的真 1:1（不受文件 DPI 元数据影响）
            PreviewImage.Stretch = Stretch.Fill;
            PreviewImage.Width = bitmap.PixelSize.Width;
            PreviewImage.Height = bitmap.PixelSize.Height;
        }
        ApplyCropTransform();
    }

    // ---- 全图导航器：缩略图 + 取样位置高亮框，拖动直接跳转 ----

    private void UpdateThumbnail()
    {
        if (DataContext is not ExportViewModel viewModel) return;
        var bitmap = viewModel.PreviewOriginal;
        ThumbImage.Source = bitmap;
        (_thumbScale, _thumbOffset) = CalculateThumbLayout(
            ThumbViewportSize, new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height));
        _cropCenter ??= new Point(bitmap.PixelSize.Width / 2.0, bitmap.PixelSize.Height / 2.0);
        ApplyCropTransform();
    }

    /// <summary>
    /// 缩略图信箱布局：等比缩放装进容器（不放大超过 1:1），返回缩放与内容区左上偏移。
    /// internal 供单测。
    /// </summary>
    internal static (double Scale, Point Offset) CalculateThumbLayout(Size container, Size image)
    {
        if (image.Width <= 0 || image.Height <= 0) return (1, default);
        double scale = Math.Min(1, Math.Min(container.Width / image.Width, container.Height / image.Height));
        return (scale, new Point(
            (container.Width - image.Width * scale) / 2,
            (container.Height - image.Height * scale) / 2));
    }

    /// <summary>缩略图坐标 → 图片像素坐标（越界钳到图内）。internal 供单测。</summary>
    internal static Point ThumbPointToImage(Point thumbPoint, double scale, Point offset, Size imageSize)
    {
        double x = Math.Clamp((thumbPoint.X - offset.X) / scale, 0, imageSize.Width);
        double y = Math.Clamp((thumbPoint.Y - offset.Y) / scale, 0, imageSize.Height);
        return new Point(x, y);
    }

    private void MoveSampleTo(Point thumbPoint)
    {
        if (ThumbImage.Source is not Bitmap bitmap) return;
        _cropCenter = ThumbPointToImage(
            thumbPoint, _thumbScale, _thumbOffset, new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height));
        ApplyCropTransform();
    }

    private void OnThumbPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ThumbViewport).Properties.IsLeftButtonPressed) return;
        _thumbDragging = true;
        MoveSampleTo(e.GetPosition(ThumbViewport)); // 按下即跳转，拖动继续跟随
        e.Pointer.Capture(ThumbViewport);
        e.Handled = true;
    }

    private void OnThumbPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_thumbDragging) return;
        MoveSampleTo(e.GetPosition(ThumbViewport));
        e.Handled = true;
    }

    private void OnThumbPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_thumbDragging) return;
        _thumbDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ApplyCropTransform()
    {
        // 用缩略图源做基准：窗口一打开就有（预览位图要等首轮预估），取样框位置不空窗
        if (ThumbImage.Source is not Bitmap bitmap) return;
        var imageSize = new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        var translate = CalculateCropTranslate(PreviewViewportSize, imageSize, _cropCenter);
        _cropTranslate.X = translate.X;
        _cropTranslate.Y = translate.Y;
        UpdateSampleRect(translate, imageSize);
    }

    /// <summary>把 1:1 视口可见区映射到导航器缩略图上，摆放取样高亮框。</summary>
    private void UpdateSampleRect(Point translate, Size imageSize)
    {
        double visLeft = Math.Max(0, -translate.X);
        double visTop = Math.Max(0, -translate.Y);
        double visRight = Math.Min(imageSize.Width, -translate.X + PreviewViewportSize.Width);
        double visBottom = Math.Min(imageSize.Height, -translate.Y + PreviewViewportSize.Height);
        ThumbSampleRect.Margin = new Thickness(
            _thumbOffset.X + visLeft * _thumbScale, _thumbOffset.Y + visTop * _thumbScale, 0, 0);
        ThumbSampleRect.Width = Math.Max(0, (visRight - visLeft) * _thumbScale);
        ThumbSampleRect.Height = Math.Max(0, (visBottom - visTop) * _thumbScale);
    }

    /// <summary>
    /// 取样窗平移：让取样中心对准视口中央；拖出边界时钳制到图像边缘；图小于视口时整体居中。
    /// 返回值是图像相对视口的原点偏移（Stretch=None 下即 RenderTransform 平移量）。internal 供单测。
    /// </summary>
    internal static Point CalculateCropTranslate(Size viewport, Size image, Point? center)
    {
        if (image.Width <= 0 || image.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
            return default;
        double cx = center?.X ?? image.Width / 2;
        double cy = center?.Y ?? image.Height / 2;
        double x = image.Width <= viewport.Width
            ? (viewport.Width - image.Width) / 2
            : Math.Clamp(viewport.Width / 2 - cx, viewport.Width - image.Width, 0);
        double y = image.Height <= viewport.Height
            ? (viewport.Height - image.Height) / 2
            : Math.Clamp(viewport.Height / 2 - cy, viewport.Height - image.Height, 0);
        return new Point(x, y);
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PreviewViewport).Properties.IsLeftButtonPressed) return;
        _showingOriginal = true;
        _lastPointer = e.GetPosition(PreviewViewport);
        UpdatePreview();
        e.Pointer.Capture(PreviewViewport);
        e.Handled = true;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_showingOriginal || PreviewImage.Source is not Bitmap bitmap) return;
        var position = e.GetPosition(PreviewViewport);
        var delta = position - _lastPointer;
        _lastPointer = position;
        var center = _cropCenter ?? new Point(bitmap.PixelSize.Width / 2.0, bitmap.PixelSize.Height / 2.0);
        // 拖动方向与取样窗相反：往右拖 = 查看更靠左的内容
        _cropCenter = new Point(center.X - delta.X, center.Y - delta.Y);
        ApplyCropTransform();
        e.Handled = true;
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_showingOriginal) return;
        _showingOriginal = false;
        UpdatePreview();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
