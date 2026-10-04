using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Inpaint.App.Services;
using Inpaint.App.ViewModels;

namespace Inpaint.App;

public partial class MainWindow : Window
{
    private const double MinHistoryPanelWidth = 200;
    private const double MaxHistoryPanelWidth = 560;

    private readonly DispatcherTimer _brushPreviewTimer = new();
    private bool _brushSliderDragging;
    private SettingsWindow? _settingsWindow;

    /// <summary>settings 为运行期共享单实例（App 传入）；测试可直接构造，默认走全新默认设置、不读磁盘。</summary>
    public MainWindow() : this(null)
    {
    }

    public MainWindow(AppSettings? settings)
    {
        InitializeComponent();
        var topLevel = TopLevel.GetTopLevel(this);
        var viewModel = new MainWindowViewModel(topLevel?.StorageProvider, topLevel?.Clipboard, settings);
        DataContext = viewModel;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 画布落笔后标记遮罩非空：Enter 触发修复的「已涂抹」门槛
        Editor.StrokePainted += (_, _) => viewModel.MarkMaskPainted();
        // 「松手即修复」：一笔涂完自动执行修复（开关与门槛都在 ViewModel 内把关）
        Editor.StrokeCommitted += (_, _) => viewModel.OnStrokeCommitted();

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

        // 拖动历史面板左缘调宽（向左拖变宽）；面板在 Auto 列里，宽度即列宽
        HistoryResizeThumb.DragDelta += (_, e) =>
        {
            HistoryPanel.Width = Math.Clamp(
                HistoryPanel.Width - e.Vector.X, MinHistoryPanelWidth, MaxHistoryPanelWidth);
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

    /// <summary>
    /// 打开设置窗口：非模态（可边改实时生效项边操作主窗口）、单实例（重复打开只置前不新建）。
    /// internal 供单测。
    /// </summary>
    internal SettingsWindow? OpenSettings()
    {
        if (DataContext is not MainWindowViewModel viewModel) return null;
        if (_settingsWindow is { } open)
        {
            open.Activate();
            return open;
        }
        var settingsWindow = new SettingsWindow(viewModel.Settings);
        // 关闭后窗口不可复用，清引用让下次点击重建
        settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = settingsWindow;
        // Show(owner)：非模态且归属主窗口（居中、随主窗口激活而置前），Owner 属性本身是 protected
        settingsWindow.Show(this);
        return settingsWindow;
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => OpenSettings();

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
