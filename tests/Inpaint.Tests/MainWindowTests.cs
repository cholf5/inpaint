using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Inpaint.App;
using Inpaint.App.Controls;
using Inpaint.App.Localization;
using Inpaint.App.ViewModels;

namespace Inpaint.Tests;

/// <summary>主窗口级集成：真实 XAML（含 KeyBinding 解析）+ 代码后置事件接线。</summary>
public class MainWindowTests
{
    [AvaloniaFact]
    public void Bracket快捷键_调整画笔大小并短暂显示预览环()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            window.Focus();
            var editor = window.GetVisualDescendants().OfType<ImageEditorControl>().Single();
            var vm = Assert.IsType<MainWindowViewModel>(window.DataContext);
            Assert.Equal(40, vm.BrushSize);

            window.KeyPress(Key.OemOpenBrackets, RawInputModifiers.None, PhysicalKey.BracketLeft, null);
            Dispatcher.UIThread.RunJobs();
            // 步进 1（更细的调节）
            Assert.Equal(39, vm.BrushSize);
            // 按键时指针不在画布上，靠预览环给出大小反馈
            Assert.True(editor.ShowSizePreview);

            window.KeyPress(Key.OemCloseBrackets, RawInputModifiers.None, PhysicalKey.BracketRight, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(40, vm.BrushSize);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void 画布滚轮_按滚轮步进调整画笔大小()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var editor = window.GetVisualDescendants().OfType<ImageEditorControl>().Single();
            var vm = Assert.IsType<MainWindowViewModel>(window.DataContext);
            vm.AdoptBitmap(new WriteableBitmap(new PixelSize(6, 4), new Vector(96, 96), PixelFormats.Bgra8888));
            Assert.Equal(40, vm.BrushSize);
            window.CaptureRenderedFrame(); // 先渲染一次，布局完成后再取画布在窗口中的坐标
            var center = editor.TranslatePoint(
                new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;

            // 上滚放大 / 下滚缩小；delta 乘滚轮步进（比 [ ] 快捷键大）
            window.MouseWheel(center, new Vector(0, 1), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(40 + MainWindowViewModel.BrushWheelStep, vm.BrushSize);

            window.MouseWheel(center, new Vector(0, -2), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(40 - MainWindowViewModel.BrushWheelStep, vm.BrushSize);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void 设置窗口_非模态单实例_关闭后可重新打开()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var first = window.OpenSettings();
            Assert.NotNull(first);
            Assert.True(first.IsVisible);

            // 重复点击设置：复用同一窗口（置前），不新建
            Assert.Same(first, window.OpenSettings());

            // 关闭后引用被清掉，下次点击重建新窗口
            first.Close();
            var second = window.OpenSettings();
            Assert.NotNull(second);
            Assert.NotSame(first, second);
            second.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Enter快捷键_绑定到需涂抹的修复命令_未涂抹时按下无效果()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            window.Focus();
            var vm = Assert.IsType<MainWindowViewModel>(window.DataContext);

            // XAML 键绑定接线：Enter → InpaintByEnterCommand（涂抹门槛在命令 CanExecute 上）
            var binding = Assert.Single(window.KeyBindings, b => b.Gesture?.Key == Key.Enter);
            Assert.Same(vm.InpaintByEnterCommand, binding.Command);

            vm.AdoptBitmap(new WriteableBitmap(new PixelSize(6, 4), new Vector(96, 96), PixelFormats.Bgra8888));
            Assert.False(vm.InpaintByEnterCommand.CanExecute(null));

            // 未涂抹按下 Enter：不触发修复（不进入忙碌，状态停在加载提示，默认为松手即修复文案）
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsBusy);
            Assert.Equal(string.Format(Translations.Instance.LoadedStatusAuto, 6, 4), vm.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlV按键_触发粘贴命令_空剪贴板给状态提示()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            window.Focus();
            var vm = Assert.IsType<MainWindowViewModel>(window.DataContext);
            // headless 剪贴板替身无数据：按键应走完整粘贴链路并以「剪贴板中没有图片」落状态
            // （⌘ 在 macOS 上是 Meta，处理逻辑与 Ctrl 同路，这里顺带覆盖 Meta 修饰键）
            window.KeyPress(Key.V, RawInputModifiers.Meta, PhysicalKey.V, null);
            await vm.PasteCommand.ExecutionTask!;

            Assert.Equal(Translations.Instance.ClipboardNoImage, vm.StatusText);
            Assert.False(vm.HasImage);
        }
        finally
        {
            await window.Clipboard!.ClearAsync(); // headless 剪贴板跨测试共享，清场防泄漏
            window.Close();
        }
    }

    [AvaloniaFact]
    public void 画布涂抹后_Enter修复命令变为可用()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var vm = Assert.IsType<MainWindowViewModel>(window.DataContext);
            vm.AdoptBitmap(new WriteableBitmap(new PixelSize(6, 4), new Vector(96, 96), PixelFormats.Bgra8888));

            // 真实指针路径落笔：MainWindow 把 Editor.StrokePainted 接到 MarkMaskPainted
            var editor = window.GetVisualDescendants().OfType<ImageEditorControl>().Single();
            window.CaptureRenderedFrame(); // 先渲染一次，Render 里才算出指针换算所需的 _contentRect
            var center = editor.TranslatePoint(
                new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.HasMaskStrokes);
            Assert.True(vm.InpaintByEnterCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void 松手即修复_默认开启自动执行_关闭后不触发()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var vm = Assert.IsType<MainWindowViewModel>(window.DataContext);
            vm.AdoptBitmap(new WriteableBitmap(new PixelSize(6, 4), new Vector(96, 96), PixelFormats.Bgra8888));
            int triggered = 0;
            vm.AutoInpaintExecutor = () => triggered++; // 替身避免真实推理/模型下载

            var editor = window.GetVisualDescendants().OfType<ImageEditorControl>().Single();
            window.CaptureRenderedFrame();
            var center = editor.TranslatePoint(
                new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;

            // 默认开启：松手即触发
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, triggered);

            // 设置窗口实时改开关（共享单实例设置）：关闭后松手不触发
            vm.Settings.InpaintOnStrokeRelease = false;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, triggered);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void 清除涂抹_仅画布残留涂抹时显示_修复按钮按开关隐藏()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var vm = Assert.IsType<MainWindowViewModel>(window.DataContext);
            Assert.True(vm.Settings.InpaintOnStrokeRelease); // 默认开
            vm.AutoInpaintExecutor = () => { }; // 松手触发走替身，避免真实推理
            var inpaintButton = window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Command == vm.InpaintCommand);
            var clearButton = window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Command == vm.ClearMaskCommand);

            vm.AdoptBitmap(new WriteableBitmap(new PixelSize(6, 4), new Vector(96, 96), PixelFormats.Bgra8888));
            Dispatcher.UIThread.RunJobs();
            // 修复按钮按开关隐藏；无涂抹时清除按钮也不显示
            Assert.False(inpaintButton.IsVisible);
            Assert.False(clearButton.IsVisible);

            // 真实指针涂抹后遮罩残留：清除按钮出现
            var editor = window.GetVisualDescendants().OfType<ImageEditorControl>().Single();
            window.CaptureRenderedFrame();
            var center = editor.TranslatePoint(
                new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(clearButton.IsVisible);

            // 松手（自动修复替身消费遮罩前遮罩仍在）→ 清除后按钮消失；修复成功路径同理
            window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(clearButton.IsVisible);
            vm.ClearMaskCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(clearButton.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
