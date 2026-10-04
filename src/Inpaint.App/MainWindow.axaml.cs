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

        // macOS 上 ⌘ 是 Meta，XAML 里的 Ctrl+O/S/Z 只认物理 Ctrl（KeyGesture 修饰键须精确匹配）：
        // 补 ⌘ 系绑定，与缩放/粘贴 OnKeyDown 的 Ctrl||⌘ 双认保持一致；键帽显示见 SettingsViewModel
        if (OperatingSystem.IsMacOS())
        {
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.O, KeyModifiers.Meta), Command = viewModel.OpenCommand });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Meta), Command = viewModel.SaveCommand });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, KeyModifiers.Meta), Command = viewModel.UndoCommand });
        }

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 超大超分二次确认：VM 在执行前询问，这里弹模态确认窗（Owner 是 protected，只能走 ShowDialog(owner) 重载）
        viewModel.ConfirmUpscaleAsync = message => new ConfirmWindow(message).ShowDialog<bool>(this);

        // 导出对话框：VM 在保存前询问格式与质量（返回的 choice 里已带编码好的字节），null = 用户取消
        viewModel.ExportDialogProvider = (node, initial) =>
            new ExportWindow(node.Image, initial).ShowDialog<ExportChoice?>(this);

        // 画布落笔后标记遮罩非空：Enter 触发修复的「已涂抹」门槛
        Editor.StrokePainted += (_, _) => viewModel.MarkMaskPainted();
        // 「松手即修复」：一笔涂完自动执行修复（开关与门槛都在 ViewModel 内把关）
        Editor.StrokeCommitted += (_, _) => viewModel.OnStrokeCommitted();

        // 滚轮在画布上直接调画笔大小（每档步进比 [ ] 快捷键大）；VM 钳制到滑块范围
        Editor.BrushSizeWheel += (_, delta) =>
            viewModel.AdjustBrushSize(delta * MainWindowViewModel.BrushWheelStep);

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

    // ---- 画布缩放/平移的键盘入口（视图操作走控件，不经 ViewModel 命令）----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        // 空格按住 = 临时平移模式（拖拽中松开不打断，直至鼠标抬起）
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            Editor.SetPanMode(true);
            return;
        }

        // 画笔大小备用键 - / =：非美式键盘布局上 [ ] 未必有直接键位（法语 AZERTY 的 + 还须 Shift），Shift 一并接受
        if (e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift
            && DataContext is MainWindowViewModel brushVm)
        {
            var brushCommand = e.Key switch
            {
                Key.OemMinus => brushVm.DecreaseBrushSizeCommand,
                Key.OemPlus => brushVm.IncreaseBrushSizeCommand,
                _ => null,
            };
            if (brushCommand is { } command && command.CanExecute(null))
            {
                command.Execute(null);
                e.Handled = true;
                return;
            }
        }

        // macOS 上 ⌘ 对应 Meta：Ctrl/⌘ + 加减号步进缩放，0 适应窗口，1 实际大小，V 粘贴，C 复制当前画布图片
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!ctrl) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                Editor.ZoomIn();
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                Editor.ZoomOut();
                e.Handled = true;
                break;
            case Key.D0:
                Editor.FitToWindow();
                e.Handled = true;
                break;
            case Key.D1:
                Editor.SetActualSize();
                e.Handled = true;
                break;
            case Key.V:
                // 手动触发须先过 CanExecute（命令绑定才检查，AsyncRelayCommand.Execute 不检查）
                if (DataContext is MainWindowViewModel viewModel && viewModel.PasteCommand.CanExecute(null))
                    viewModel.PasteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.C:
                // 复制当前预览的历史节点（与历史节点右键菜单同一命令）；无图或繁忙时命令本就禁用
                if (DataContext is MainWindowViewModel copyVm && copyVm.CopyNodeCommand.CanExecute(copyVm.CurrentNode))
                    copyVm.CopyNodeCommand.Execute(copyVm.CurrentNode);
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.Space) Editor.SetPanMode(false);
    }

    // 缩放胶囊按钮
    private void OnZoomInClick(object? sender, RoutedEventArgs e) => Editor.ZoomIn();
    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => Editor.ZoomOut();
    private void OnZoomActualClick(object? sender, RoutedEventArgs e) => Editor.SetActualSize();
    private void OnZoomFitClick(object? sender, RoutedEventArgs e) => Editor.FitToWindow();

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
