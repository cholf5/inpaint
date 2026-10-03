using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Inpaint.App.ViewModels;

namespace Inpaint.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainWindowViewModel(StorageProvider);
        DataContext = viewModel;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 拖动画笔大小滑块期间，指针虽在滑块上，也持续在画布上显示画笔大小预览环
        BrushSlider.AddHandler(Thumb.DragStartedEvent, (_, _) => Editor.ShowSizePreview = true);
        BrushSlider.AddHandler(Thumb.DragCompletedEvent, (_, _) => Editor.ShowSizePreview = false);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        if (e.DataTransfer.TryGetFiles() is not { } files) return;
        var path = files
            .Select(file => file.Path.LocalPath)
            .FirstOrDefault(File.Exists);
        if (path is not null)
            await viewModel.LoadFromPathAsync(path);
    }
}
