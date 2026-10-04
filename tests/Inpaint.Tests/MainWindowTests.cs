using Avalonia;
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

            // 未涂抹按下 Enter：不触发修复（不进入忙碌，状态停在「已加载…」）
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsBusy);
            Assert.Equal(
                string.Format(Translations.Instance.LoadedStatus, 6, 4, Translations.Instance.Inpaint),
                vm.StatusText);
        }
        finally
        {
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
}
