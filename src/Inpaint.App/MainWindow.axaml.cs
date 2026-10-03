using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Inpaint.App.ViewModels;

namespace Inpaint.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _brushPreviewTimer = new();
    private bool _brushSliderDragging;

    public MainWindow()
    {
        InitializeComponent();
        var topLevel = TopLevel.GetTopLevel(this);
        var viewModel = new MainWindowViewModel(topLevel?.StorageProvider, topLevel?.Clipboard);
        DataContext = viewModel;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 拖动画笔大小滑块期间，指针虽在滑块上，也持续在画布上显示画笔大小预览环
        BrushSlider.AddHandler(Thumb.DragStartedEvent, (_, _) =>
        {
            _brushSliderDragging = true;
            Editor.ShowSizePreview = true;
        });
        BrushSlider.AddHandler(Thumb.DragCompletedEvent, (_, _) =>
        {
            _brushSliderDragging = false;
            Editor.ShowSizePreview = false;
        });

        // 按 [ / ] 快捷键调整画笔大小时，指针多半不在画布上，短暂显示大小预览环作反馈
        _brushPreviewTimer.Interval = TimeSpan.FromMilliseconds(800);
        _brushPreviewTimer.Tick += (_, _) =>
        {
            _brushPreviewTimer.Stop();
            Editor.ShowSizePreview = false;
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not nameof(MainWindowViewModel.BrushSize) || _brushSliderDragging) return;
            Editor.ShowSizePreview = true;
            _brushPreviewTimer.Stop();
            _brushPreviewTimer.Start();
        };

        // 生成历史更新后，把当前预览的节点滚进可视区（新节点常在图底部/右侧）
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not nameof(MainWindowViewModel.CurrentNode)) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (viewModel.CurrentNode is { } node) HistoryGraph.ScrollNodeIntoView(node);
            }, DispatcherPriority.Loaded);
        };
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
