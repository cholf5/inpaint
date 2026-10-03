using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Inpaint.App;
using Inpaint.App.Controls;
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
            Assert.Equal(30, vm.BrushSize);
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
}
