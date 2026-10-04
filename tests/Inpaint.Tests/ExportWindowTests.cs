using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Inpaint.App;
using Inpaint.App.Services;
using Inpaint.App.ViewModels;

namespace Inpaint.Tests;

/// <summary>导出对话框：渲染冒烟 + 1:1 取样预览（按住对比原图、拖动取样、裁剪平移钳制）。</summary>
public class ExportWindowTests
{
    [AvaloniaFact]
    public async Task 打开对话框_真实渲染并完成首轮预估()
    {
        var bmp = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormats.Bgra8888);
        var window = new ExportWindow(bmp, new ExportOptions(ExportFormat.Jpeg, 70));

        ExportChoice? received = null;
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
            var vm = Assert.IsType<ExportViewModel>(window.DataContext);
            Assert.True(vm.IsQualityVisible); // 初始 Jpeg：质量区块可见
            Assert.Equal(70, vm.QualitySlider);

            // 等首轮预估完成（窗口 OnOpened 启动），确认链路可用
            await vm.EstimateTask;
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.False(vm.IsEstimating);
            Assert.NotNull(vm.EstimatedSize);

            vm.ExportConfirmed += choice => received = choice;
            vm.ConfirmCommand.Execute(null);
            await vm.ConfirmCommand.ExecutionTask!;
            Assert.NotNull(received);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task 预览_预估完成后显示解码结果_按住切换原图()
    {
        var bmp = new WriteableBitmap(new PixelSize(600, 400), new Vector(96, 96), PixelFormats.Bgra8888);
        var window = new ExportWindow(bmp, new ExportOptions(ExportFormat.Jpeg, 70));
        window.Show();
        try
        {
            var vm = Assert.IsType<ExportViewModel>(window.DataContext);
            await vm.EstimateTask;
            await Dispatcher.UIThread.InvokeAsync(() => { });

            // 预览 = 解码自预估字节，与原图同尺寸但是另一实例；占位隐藏
            Assert.NotNull(window.PreviewImage.Source);
            Assert.NotSame(bmp, window.PreviewImage.Source);
            Assert.False(window.PreviewPlaceholder.IsVisible);

            // 按住视口 = 切原图；松开 = 回压缩结果
            var pressAt = window.PreviewViewport.TranslatePoint(new Point(150, 100), window)!.Value;
            window.MouseDown(pressAt, MouseButton.Left);
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Same(vm.PreviewOriginal, window.PreviewImage.Source);

            window.MouseUp(pressAt, MouseButton.Left, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Same(vm.PreviewResult, window.PreviewImage.Source);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task 预览_按住拖动移动取样位置且钳制到边缘()
    {
        var bmp = new WriteableBitmap(new PixelSize(600, 400), new Vector(96, 96), PixelFormats.Bgra8888);
        var window = new ExportWindow(bmp, new ExportOptions(ExportFormat.Jpeg, 70));
        window.Show();
        try
        {
            var vm = Assert.IsType<ExportViewModel>(window.DataContext);
            await vm.EstimateTask;
            await Dispatcher.UIThread.InvokeAsync(() => { });
            var viewport = window.PreviewViewport;
            var center = viewport.TranslatePoint(new Point(150, 100), window)!.Value;

            // 按住向左拖 60：取样中心右移 60（拖动方向与取样窗相反），初始中心 = 图像中心 (300,200)
            window.MouseDown(center, MouseButton.Left);
            window.MouseMove(center - new Point(60, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(center - new Point(60, 0), MouseButton.Left, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(() => { });

            // 600×400 图在 300×200 视口里：中心 (360,200) → 平移 = (150-360, 100-200)
            var translate = (Avalonia.Media.TranslateTransform)window.PreviewImage.RenderTransform!;
            Assert.Equal(-210, translate.X, 1);
            Assert.Equal(-100, translate.Y, 1);

            // 拖出左边界：取样中心钳到图像左缘 → 平移回 0
            window.MouseDown(center, MouseButton.Left);
            window.MouseMove(center + new Point(400, 0), RawInputModifiers.LeftMouseButton);
            window.MouseUp(center + new Point(400, 0), MouseButton.Left, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(() => { });
            translate = (Avalonia.Media.TranslateTransform)window.PreviewImage.RenderTransform!;
            Assert.Equal(0, translate.X, 1);
        }
        finally
        {
            window.Close();
        }
    }

    [Theory]
    [InlineData(500, 400, 0, 0, false, -100)]      // null 中心 = 图像中心 (250,200)：x = 150-250，钳到界内
    [InlineData(500, 400, -999, -999, true, 0)]    // 拖出左/上边界钳到 0
    [InlineData(500, 400, 9999, 9999, true, -200)] // 拖出右/下边界钳到 (300-500, 200-400)
    [InlineData(200, 100, 0, 0, true, 50)]         // 图小于视口整体居中（x=(300-200)/2, y=(200-100)/2）
    public void CalculateCropTranslate_取样窗平移钳制(
        double iw, double ih, double cx, double cy, bool hasCenter, double expectedX)
    {
        Point? center = hasCenter ? new Point(cx, cy) : null;
        var translate = ExportWindow.CalculateCropTranslate(
            ExportWindow.PreviewViewportSize, new Size(iw, ih), center);
        Assert.Equal(expectedX, translate.X, 1);
        // y 与 x 同规则（上面交互测试已覆盖 y），这里只锁 x 防回归
    }

    [Theory]
    [InlineData(800, 600, 0.2, 70, 0)]   // 横图按高收窄：0.2 → 内容 160×120 水平居中
    [InlineData(100, 50, 1, 100, 35)]    // 小图不放大超过 1:1，整体居中
    [InlineData(400, 1000, 0.12, 126, 0)] // 竖图按宽收窄
    public void CalculateThumbLayout_信箱布局与1比1上限(
        double iw, double ih, double scale, double ox, double oy)
    {
        var (s, offset) = ExportWindow.CalculateThumbLayout(
            ExportWindow.ThumbViewportSize, new Size(iw, ih));
        Assert.Equal(scale, s, 3);
        Assert.Equal(ox, offset.X, 1);
        Assert.Equal(oy, offset.Y, 1);
    }

    [Fact]
    public void ThumbPointToImage_坐标映射与越界钳制()
    {
        // scale 0.2 / offset (70,0)：内容区中心 (160,60) → 图 (450, 300)
        var center = ExportWindow.ThumbPointToImage(new Point(160, 60), 0.2, new Point(70, 0), new Size(800, 600));
        Assert.Equal(450, center.X, 1);
        Assert.Equal(300, center.Y, 1);
        // 信箱留白区钳到图内
        var corner = ExportWindow.ThumbPointToImage(new Point(0, 0), 0.2, new Point(70, 0), new Size(800, 600));
        Assert.Equal(0, corner.X, 1);
        Assert.Equal(0, corner.Y, 1);
    }

    [AvaloniaFact]
    public async Task 导航器_打开即显示取样框_拖动跳转并同步高亮框()
    {
        var bmp = new WriteableBitmap(new PixelSize(600, 400), new Vector(96, 96), PixelFormats.Bgra8888);
        var window = new ExportWindow(bmp, new ExportOptions(ExportFormat.Jpeg, 70));
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
            // 600×400 → scale 0.3、内容偏移 (60,0)；默认取样中心 = 图像中心 → 高亮框 (105,30)、90×60
            var rect = window.ThumbSampleRect;
            Assert.Equal(105, rect.Margin.Left, 1);
            Assert.Equal(30, rect.Margin.Top, 1);
            Assert.Equal(90, rect.Width, 1);
            Assert.Equal(60, rect.Height, 1);
            var translate = (Avalonia.Media.TranslateTransform)window.PreviewImage.RenderTransform!;
            Assert.Equal(-150, translate.X, 1); // 1:1 视口同步居中

            // 拖到内容区右下角附近 (239,119)（避开视口边界像素）→ 图像 ≈(597,397) → 平移钳到 (-300,-200)，高亮框右下贴边
            var thumb = window.ThumbViewport.TranslatePoint(new Point(239, 119), window)!.Value;
            window.MouseDown(thumb, MouseButton.Left);
            window.MouseUp(thumb, MouseButton.Left, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.Equal(-300, translate.X, 1);
            Assert.Equal(-200, translate.Y, 1);
            Assert.Equal(150, rect.Margin.Left, 1);  // 60 + 300×0.3
            Assert.Equal(60, rect.Margin.Top, 1);    // 0 + 200×0.3
        }
        finally
        {
            window.Close();
        }
    }
}

