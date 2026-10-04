using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Inpaint.App.Controls;
using Inpaint.App.Localization;
using Inpaint.App.Services;
using Inpaint.Core;
using Inpaint.Inference;

namespace Inpaint.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const int ThumbnailMaxSide = 96;

    /// <summary>
    /// 修复模型单次推理的像素上限。MI-GAN 是全分辨率一次推理，超过后模型内存与耗时都会失控，
    /// 明确报错优于 OOM 崩溃/永久假死。internal set 供单测注入小值验证 guard 链路。
    /// </summary>
    internal long InpaintMaxPixels { get; set; } = 32_000_000;

    /// <summary>
    /// 超分输入像素上限。×4 输出的峰值内存约 200 字节/输入像素（float 输出 + BGRA 转换 + 位图中转），
    /// 32MP 输入约需 6GB 峰值，且 float 输出数组长度逼近 int 上限。internal set 供单测注入小值。
    /// </summary>
    internal long UpscaleMaxPixels { get; set; } = 32_000_000;

    /// <summary>
    /// 超分确认阈值（按 ×4 后的输出像素计）：输出超 1 亿像素（约 11300×11300，结果位图 ≥400MB，
    /// 加推理峰值可达数 GB）时先弹窗确认再执行，防手滑连点拖垮整机内存（硬上限仍由 UpscaleMaxPixels 兜底）。
    /// 100MP 以下不打扰——普通 AI 图与中等照片 ×4 后都在其内。internal set 供单测注入小值。
    /// </summary>
    internal long UpscaleConfirmPixels { get; set; } = 100_000_000;

    /// <summary>
    /// 历史位图总字节预算（BGRA 体积合计）：MaxHistory 节点数之外的第二道约束，
    /// 超大分辨率图上 25 张全分辨率位图可达数 GB，会触发系统级内存压力。internal set 供单测注入小值。
    /// </summary>
    internal long HistoryByteBudget { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>画笔大小范围（与 MainWindow 滑块一致）。internal 供单测。</summary>
    internal const double MinBrushSize = 4;
    internal const double MaxBrushSize = 160;
    private const double BrushSizeStep = 1;

    /// <summary>滚轮在画布上调节画笔的每档步进（滚轮要转得比 [ ] 快捷键快）。internal 供单测。</summary>
    internal const double BrushWheelStep = 4;

    private static readonly string[] ImagePatterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"];

    private readonly IStorageProvider? _storage;
    private readonly IClipboard? _clipboard;
    private readonly AppSettings _settings;
    private ImageHistoryNode? _root;
    /// <summary>当前历史树源文件的基名（去扩展名），保存对话框的默认文件名由它派生。</summary>
    private string? _sourceFileName;
    /// <summary>源文件所在目录：首次保存的起始位置与重名探测目录。</summary>
    private string? _sourceDirectory;
    /// <summary>上次保存所选目录：后续保存的起始位置与重名探测目录。</summary>
    private string? _lastSaveDirectory;
    /// <summary>上次导出设置（格式 + 质量）：再次打开导出对话框时的初始值。</summary>
    private ExportOptions? _lastExportOptions;
    private InpaintEngine? _inpaintEngine;
    private UpscaleEngine? _upscaleEngine;
    private bool _resetUpscaleWhenIdle;

    [ObservableProperty] private Bitmap? _currentImage;
    [ObservableProperty] private MaskLayer? _maskLayer;
    [ObservableProperty] private double _brushSize = 40;
    [ObservableProperty] private double _progress;
    // 状态栏绑定的是派生属性 StatusDisplay，StatusText 变更必须连带通知，否则瞬态提示不刷新
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    private string? _statusText;
    [ObservableProperty] private IReadOnlyList<ImageHistoryNode> _historyNodes = [];
    [ObservableProperty] private bool _isHistoryVisible = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private ImageHistoryNode? _currentNode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(InpaintCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpscaleCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearMaskCommand))]
    private bool _hasImage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(InpaintCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpscaleCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearMaskCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyNodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveNodeCommand))]
    [NotifyPropertyChangedFor(nameof(ShowClearMask))]
    private bool _isBusy;

    /// <summary>当前遮罩是否已有涂抹（画布落笔置位，SetMask 重建遮罩时复位）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InpaintByEnterCommand))]
    [NotifyPropertyChangedFor(nameof(ShowClearMask))]
    private bool _hasMaskStrokes;

    public MainWindowViewModel(IStorageProvider? storage, IClipboard? clipboard, AppSettings? settings = null)
    {
        _storage = storage;
        _clipboard = clipboard;
        _settings = settings ?? new AppSettings();
        // 画笔初始值来自设置（钳制到滑块范围）；后续设置变更经 OnSettingsChanged 同步
        _brushSize = Math.Clamp(_settings.DefaultBrushSize, MinBrushSize, MaxBrushSize);
        _settings.PropertyChanged += OnSettingsChanged;
        // 语言切换时刷新历史面板标题、初始提示这类派生文本；瞬态状态文本保持出现时的语言直到下次更新
        Translations.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HistoryTitle));
            OnPropertyChanged(nameof(StatusDisplay));
        };
    }

    /// <summary>历史面板标题（含节点计数）；历史或语言变化时刷新。</summary>
    public string HistoryTitle => string.Format(Translations.Instance.HistoryTitleFormat, HistoryNodes.Count);

    /// <summary>状态栏显示文本：无瞬态状态时显示初始提示（作为派生文本随语言切换刷新）。</summary>
    public string StatusDisplay => StatusText ?? Translations.Instance.InitialStatus;

    /// <summary>共享设置实例（设置窗口直接编辑它，主窗口经设备/画笔订阅响应变更）。</summary>
    public AppSettings Settings => _settings;

    partial void OnHistoryNodesChanged(IReadOnlyList<ImageHistoryNode> value) =>
        OnPropertyChanged(nameof(HistoryTitle));

    /// <summary>
    /// 设置窗口实时修改：超分设备变化丢弃已建会话（下次运行按新设备重建），
    /// 默认画笔大小变化同步到当前画笔。internal 供单测。
    /// </summary>
    internal void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.UpscaleDevice):
                ResetUpscaleEngineWhenIdle();
                break;
            case nameof(AppSettings.DefaultBrushSize):
                BrushSize = Math.Clamp(_settings.DefaultBrushSize, MinBrushSize, MaxBrushSize);
                break;
            case nameof(AppSettings.InpaintOnStrokeRelease):
                OnPropertyChanged(nameof(ShowInpaintButton));
                break;
        }
    }

    /// <summary>设备切换后重建超分引擎；正在推理时先记标记，等操作结束再释放，避免释放运行中的会话。</summary>
    private void ResetUpscaleEngineWhenIdle()
    {
        if (IsBusy)
        {
            _resetUpscaleWhenIdle = true;
            return;
        }
        var old = _upscaleEngine;
        _upscaleEngine = null;
        if (old is not null) Task.Run(old.Dispose);
    }

    /// <summary>繁忙期间收到过设备切换请求时，在操作结束（IsBusy 复位）后执行延迟释放。</summary>
    private void ResetUpscaleEngineIfFlagged()
    {
        if (!_resetUpscaleWhenIdle) return;
        _resetUpscaleWhenIdle = false;
        ResetUpscaleEngineWhenIdle();
    }

    private bool NotBusy() => !IsBusy;
    private bool NotBusyAndHasImage() => !IsBusy && HasImage;
    private bool CanInpaintByEnter() => NotBusyAndHasImage() && HasMaskStrokes;
    private bool CanUndo() => !IsBusy && CurrentNode?.Parent is not null;
    private bool CanOperateNode(ImageHistoryNode? node) => !IsBusy && node is not null && _clipboard is not null;

    // ---- 图片载入 ----

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task OpenAsync()
    {
        if (_storage is null) return;
        var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Translations.Instance.PickerOpenTitle,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Translations.Instance.FileTypeImages) { Patterns = ImagePatterns }],
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        await LoadFromStreamAsync(stream, files[0].Path.LocalPath);
    }

    public async Task LoadFromPathAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        await LoadFromStreamAsync(stream, path);
    }

    public async Task LoadFromStreamAsync(Stream stream, string? sourcePath = null)
    {
        if (IsBusy) return; // 推理/加载进行中忽略新载入，避免并发改写历史树
        IsBusy = true;
        try
        {
            StatusText = Translations.Instance.LoadingImage;
            // 解码放后台：超大图（上百兆像素）的 PNG/JPEG 解码要数秒，同步跑在 UI 线程会整窗冻结
            var bitmap = await Task.Run(() => new Bitmap(stream));
            AdoptBitmap(bitmap, sourcePath);
        }
        catch (Exception e)
        {
            StatusText = string.Format(Translations.Instance.CannotOpen, e.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 接管一张位图作为新的编辑起点（开一棵新的历史树，旧树整体释放）。
    /// sourcePath 传源文件完整路径（或裸文件名），用于推导保存建议名与保存起始目录。
    /// </summary>
    public void AdoptBitmap(Bitmap bitmap, string? sourcePath = null)
    {
        if (_root is not null) DisposeTree(_root);
        // 统一在这里拆基名与目录：调用方传完整路径或裸文件名都可以，双重扩展名也不会出现
        _sourceFileName = string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : Path.GetFileNameWithoutExtension(sourcePath);
        _sourceDirectory = string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : GetDirectoryNameOrNull(sourcePath);
        var node = new ImageHistoryNode(bitmap, Translations.Instance.OriginalNode, null)
        {
            Thumbnail = CreateThumbnail(bitmap)
        };
        _root = node;
        CurrentNode = node;
        RebuildHistory();
        CurrentImage = bitmap;
        SetMask(bitmap.PixelSize);
        HasImage = true;
        // 「松手即修复」开启时工具栏没有修复按钮，提示语相应换成松手触发
        StatusText = _settings.InpaintOnStrokeRelease
            ? string.Format(Translations.Instance.LoadedStatusAuto, bitmap.PixelSize.Width, bitmap.PixelSize.Height)
            : string.Format(
                Translations.Instance.LoadedStatus,
                bitmap.PixelSize.Width, bitmap.PixelSize.Height, Translations.Instance.Inpaint);
        UndoCommand.NotifyCanExecuteChanged();
    }

    // ---- 画笔大小（滑块 / 快捷键 [ ] / 画布滚轮）----

    /// <summary>按给定增量调整画笔大小，钳制到滑块范围。internal 供单测。</summary>
    internal void AdjustBrushSize(double delta) =>
        BrushSize = Math.Clamp(BrushSize + delta, MinBrushSize, MaxBrushSize);

    /// <summary>快捷键 [：缩小画笔。</summary>
    [RelayCommand]
    private void DecreaseBrushSize() => AdjustBrushSize(-BrushSizeStep);

    /// <summary>快捷键 ]：放大画笔。</summary>
    [RelayCommand]
    private void IncreaseBrushSize() => AdjustBrushSize(BrushSizeStep);

    // ---- 修复 ----

    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private async Task InpaintAsync()
    {
        if (CurrentImage is not { } current || MaskLayer is not { } mask) return;
        var size = current.PixelSize;
        if ((long)size.Width * size.Height > InpaintMaxPixels)
        {
            StatusText = string.Format(Translations.Instance.InpaintTooLarge, size.Width, size.Height);
            return;
        }
        IsBusy = true;
        try
        {
            Progress = 0;
            StatusText = Translations.Instance.PreparingInpaint;
            _inpaintEngine ??= await InpaintEngine.CreateAsync(DownloadProgress());
            var engine = _inpaintEngine;
            StatusText = Translations.Instance.Inpainting;
            // 像素级前后处理与推理一起放后台：大图上 ExtractBgra/CHW 转换/写位图
            // 都是数百 MB 级的拷贝与循环，留在 UI 线程会在推理前后各冻结数秒
            var result = await Task.Run(() =>
            {
                var imageChw = ImageProcessing.BgraToRgbChw(ImageExporter.ExtractBgra(current), size.Width, size.Height);
                var maskChw = ImageProcessing.MaskGrayToChw(mask.Data);
                var output = engine!.Run(size.Width, size.Height, imageChw, maskChw);
                return CreateBitmap(size, ImageProcessing.RgbChwToBgra(output, size.Width, size.Height));
            });
            PushHistory(result, Translations.Instance.NodeInpaint);
            MaskLayer?.Clear();
            HasMaskStrokes = false;
            StatusText = string.Format(Translations.Instance.InpaintDone, size.Width, size.Height);
        }
        catch (Exception e)
        {
            StatusText = string.Format(Translations.Instance.InpaintFailed, e.Message);
        }
        finally
        {
            IsBusy = false;
            ResetUpscaleEngineIfFlagged();
        }
    }

    /// <summary>Enter 快捷键触发修复：仅已涂抹遮罩时可用（工具栏按钮不受此限，维持原可用条件）。</summary>
    [RelayCommand(CanExecute = nameof(CanInpaintByEnter))]
    private Task InpaintByEnterAsync() => InpaintAsync();

    /// <summary>画布落下新笔触（由 MainWindow 接线 ImageEditorControl.StrokePainted）。internal 供单测。</summary>
    internal void MarkMaskPainted() => HasMaskStrokes = true;

    /// <summary>「松手即修复」（默认开）时隐藏工具栏修复按钮，回到按钮式工作流。</summary>
    public bool ShowInpaintButton => !_settings.InpaintOnStrokeRelease;

    /// <summary>
    /// 清除涂抹无状态化：仅画布残留未处理涂抹时显示。松手即修复模式下推理成功即清遮罩，
    /// 按钮自然消失，只有失败（遮罩滞留）才浮现作逃生门；繁忙期间遮罩属「处理中」而非「悬停」，一并隐藏。
    /// </summary>
    public bool ShowClearMask => HasMaskStrokes && !IsBusy;

    /// <summary>松手自动修复的执行动作；测试注入替身避免真实推理，null 走 InpaintByEnterCommand。internal 供单测。</summary>
    internal Action? AutoInpaintExecutor;

    /// <summary>
    /// 一笔涂抹结束（由 MainWindow 接线 ImageEditorControl.StrokeCommitted）：
    /// 「松手即修复」开启且修复可用（非繁忙、有图、已涂抹，与 Enter 快捷键同门槛）时自动执行。internal 供单测。
    /// </summary>
    internal void OnStrokeCommitted()
    {
        if (!_settings.InpaintOnStrokeRelease || !InpaintByEnterCommand.CanExecute(null)) return;
        if (AutoInpaintExecutor is { } executor) executor();
        else InpaintByEnterCommand.Execute(null);
    }

    // ---- 高清放大 ----

    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private async Task UpscaleAsync()
    {
        if (CurrentImage is not { } current) return;
        var size = current.PixelSize;
        if ((long)size.Width * size.Height > UpscaleMaxPixels)
        {
            StatusText = string.Format(Translations.Instance.UpscaleTooLarge, size.Width, size.Height);
            return;
        }
        // 硬上限以内但输出极大时二次确认（确认期间 IsBusy 未置位也无碍：弹窗是模态的，主窗口不可点）
        if (!await ConfirmLargeUpscaleAsync(size))
        {
            StatusText = Translations.Instance.UpscaleCancelled;
            return;
        }
        IsBusy = true;
        try
        {
            Progress = 0;
            StatusText = Translations.Instance.PreparingUpscale;
            _upscaleEngine ??= await UpscaleEngine.CreateAsync(
                DownloadProgress(), accelerationMode: _settings.UpscaleDevice);
            var engine = _upscaleEngine;
            StatusText = string.Format(
                Translations.Instance.Upscaling, size.Width, size.Height, size.Width * 4, size.Height * 4);
            var newSize = new PixelSize(size.Width * 4, size.Height * 4);
            // 输入转换、分块推理与输出转位图整体放后台（见 InpaintAsync 中的说明）
            var result = await Task.Run(() =>
            {
                var chw = ImageProcessing.BgraToRgbChwF32(ImageExporter.ExtractBgra(current), size.Width, size.Height);
                var output = engine!.Run(size.Width, size.Height, chw, TileProgress());
                return CreateBitmap(newSize, ImageProcessing.RgbChwF32ToBgra(output, newSize.Width, newSize.Height));
            });
            PushHistory(result, Translations.Instance.NodeUpscale);
            SetMask(newSize);
            StatusText = string.Format(Translations.Instance.UpscaleDone, newSize.Width, newSize.Height);
        }
        catch (Exception e)
        {
            StatusText = string.Format(Translations.Instance.UpscaleFailed, e.Message);
        }
        finally
        {
            IsBusy = false;
            ResetUpscaleEngineIfFlagged();
        }
    }

    /// <summary>超大超分确认弹窗回调（MainWindow 接线为模态确认窗）：入参提示文案，返回 true = 继续。internal 供单测注入替身。</summary>
    internal Func<string, Task<bool>>? ConfirmUpscaleAsync;

    /// <summary>
    /// 输出像素超确认阈值时经 ConfirmUpscaleAsync 弹窗询问；未接线（无 UI 环境）安全起见按取消处理。
    /// internal 供单测。
    /// </summary>
    internal async Task<bool> ConfirmLargeUpscaleAsync(PixelSize size)
    {
        long outWidth = (long)size.Width * 4, outHeight = (long)size.Height * 4;
        if (outWidth * outHeight <= UpscaleConfirmPixels) return true;
        if (ConfirmUpscaleAsync is not { } confirm) return false;
        return await confirm(string.Format(
            Translations.Instance.UpscaleConfirmLarge,
            size.Width, size.Height, outWidth, outHeight));
    }

    // ---- 生成历史 / 遮罩 / 复制 / 保存 ----

    /// <summary>点击节点：预览该图并将其作为后续生成的新基准；基于中间节点继续生成即开新车道分叉。</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void SelectNode(ImageHistoryNode? node)
    {
        if (node is null || node == CurrentNode) return;
        SwitchTo(node, string.Format(Translations.Instance.SwitchedToNode, node.Title));
    }

    /// <summary>撤销 = 在历史树上移动到父节点。</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (CurrentNode?.Parent is not { } parent) return;
        SwitchTo(parent, Translations.Instance.Undone);
    }

    /// <summary>回到原图 = 移动到根节点；其余分支保留，可随时点击切回。</summary>
    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private void Reset()
    {
        if (_root is null || _root == CurrentNode) return;
        SwitchTo(_root, Translations.Instance.BackToOriginal);
    }

    /// <summary>右键菜单：把任意历史图复制到系统剪贴板。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateNode))]
    private async Task CopyNodeAsync(ImageHistoryNode? node)
    {
        if (node?.Image is not { } bitmap || _clipboard is null) return;
        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create<Bitmap>(DataFormat.Bitmap, bitmap));
            await _clipboard.SetDataAsync(transfer);
            StatusText = string.Format(Translations.Instance.CopiedToClipboard, node.Title);
        }
        catch (Exception e)
        {
            StatusText = string.Format(Translations.Instance.CopyFailed, e.Message);
        }
    }

    /// <summary>右键菜单：导出任意历史图（经导出对话框选择格式与质量）。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateNode))]
    private async Task SaveNodeAsync(ImageHistoryNode? node)
    {
        if (node is null) return;
        await SaveToPickerAsync(node);
    }

    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private void ClearMask()
    {
        MaskLayer?.Clear();
        HasMaskStrokes = false;
        StatusText = Translations.Instance.MaskCleared;
    }

    /// <summary>工具栏「导出」：导出当前预览图（经导出对话框选择格式与质量）。</summary>
    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private async Task SaveAsync()
    {
        if (CurrentNode is not { } node) return;
        await SaveToPickerAsync(node);
    }

    // ---- 内部工具 ----

    private void SwitchTo(ImageHistoryNode node, string status)
    {
        CurrentNode = node;
        CurrentImage = node.Image;
        SetMask(node.Image.PixelSize);
        RebuildHistory();
        UndoCommand.NotifyCanExecuteChanged();
        StatusText = status;
    }

    /// <summary>
    /// 把新结果挂到当前预览节点之下形成新历史项。git 式分叉：
    /// 当前节点还没有子节点时延续其车道；已有子节点（基于中间节点重新生成）则新子节点开新车道，
    /// 原有后续节点留在原车道不动。internal 供单测。
    /// </summary>
    internal void PushHistory(Bitmap bitmap, string title)
    {
        var node = new ImageHistoryNode(bitmap, title, CurrentNode);
        CurrentNode?.Children.Add(node);
        node.Thumbnail = CreateThumbnail(bitmap);
        _root ??= node;
        CurrentNode = node;
        RebuildHistory();
        PruneHistory();
        CurrentImage = bitmap;
        UndoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 节点数超 MaxHistory 或位图总字节超 HistoryByteBudget 时，从最旧开始丢弃不在当前路径上的节点
    /// （其子树随之整体删除，子节点向上拼接到祖父以保持其余分支连通）；
    /// 若整棵树是一条链则退而拼接最旧的非根节点。原图（根）与当前预览节点永不丢弃。
    /// 字节预算兜住超大分辨率图：节点数没超也可能已占数 GB，触发系统级内存压力。
    /// </summary>
    private void PruneHistory()
    {
        if (_root is null) return;
        bool removed = false;
        while (true)
        {
            var nodes = AllNodes(_root).ToList();
            if (nodes.Count <= _settings.MaxHistory && BitmapBytes(nodes) <= HistoryByteBudget) break;
            var path = CurrentPathSet();
            var victim = nodes
                    .Where(n => n != _root && !path.Contains(n))
                    .MinBy(n => n.Id)
                ?? nodes
                    .Where(n => n != _root && n != CurrentNode)
                    .MinBy(n => n.Id);
            if (victim is null) break;
            var parent = victim.Parent!;
            int index = parent.Children.IndexOf(victim);
            parent.Children.RemoveAt(index);
            parent.Children.InsertRange(index, victim.Children);
            foreach (var child in victim.Children) child.Parent = parent;
            victim.Thumbnail?.Dispose();
            victim.Image.Dispose();
            removed = true;
        }
        if (removed) RebuildHistory();
    }

    private static long BitmapBytes(IEnumerable<ImageHistoryNode> nodes) =>
        nodes.Sum(n => (long)n.Image.PixelSize.Width * n.Image.PixelSize.Height * 4);

    /// <summary>
    /// 重建 Graph 布局：行 = 全图按创建时间从上往下；
    /// 车道 = 第一子节点延续父节点车道，分叉子节点取最小未占用车道（按分叉发生顺序向右）。
    /// </summary>
    private void RebuildHistory()
    {
        if (_root is null)
        {
            HistoryNodes = [];
            return;
        }

        var rows = AllNodes(_root).OrderBy(n => n.Id).ToList();
        for (int i = 0; i < rows.Count; i++) rows[i].RowIndex = i;

        var path = CurrentPathSet();
        var usedLanes = new HashSet<int>();

        int TakeFreeLane()
        {
            int lane = 0;
            while (usedLanes.Contains(lane)) lane++;
            usedLanes.Add(lane);
            return lane;
        }

        void Visit(ImageHistoryNode node, int lane)
        {
            node.LaneIndex = lane;
            usedLanes.Add(lane);
            node.IsOnCurrentPath = path.Contains(node);
            node.IsCurrent = node == CurrentNode;
            for (int i = 0; i < node.Children.Count; i++)
                Visit(node.Children[i], i == 0 ? lane : TakeFreeLane());
        }
        Visit(_root, 0);

        HistoryNodes = rows;
    }

    private HashSet<ImageHistoryNode> CurrentPathSet()
    {
        var path = new HashSet<ImageHistoryNode>();
        for (var n = CurrentNode; n is not null; n = n.Parent) path.Add(n);
        return path;
    }

    private static IEnumerable<ImageHistoryNode> AllNodes(ImageHistoryNode root)
    {
        var stack = new Stack<ImageHistoryNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }

    private static void DisposeTree(ImageHistoryNode root)
    {
        var stack = new Stack<ImageHistoryNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.Children) stack.Push(child);
            node.Thumbnail?.Dispose();
            node.Image.Dispose();
        }
    }

    private static Bitmap? CreateThumbnail(Bitmap source)
    {
        try
        {
            var size = source.PixelSize;
            double scale = Math.Min(1.0, (double)ThumbnailMaxSide / Math.Max(size.Width, size.Height));
            var target = new PixelSize(
                Math.Max(1, (int)Math.Round(size.Width * scale)),
                Math.Max(1, (int)Math.Round(size.Height * scale)));
            // WriteableBitmap 源不被 CreateScaledBitmap 接受，统一走 RenderTargetBitmap 缩放绘制
            var thumbnail = new RenderTargetBitmap(target);
            using (var ctx = thumbnail.CreateDrawingContext())
                ctx.DrawImage(source, new Rect(0, 0, target.Width, target.Height));
            return thumbnail;
        }
        catch
        {
            return null; // 缩略图失败不影响生成主流程，节点仅缺小图
        }
    }

    private void SetMask(PixelSize size)
    {
        MaskLayer?.Dispose();
        MaskLayer = new MaskLayer(size);
        HasMaskStrokes = false; // 新遮罩无涂抹，Enter 修复快捷键随之回到不可用
    }

    /// <summary>
    /// 导出对话框回调（MainWindow 接线为模态 ExportWindow）：入参待导出节点与上次导出设置，
    /// 返回 null = 用户取消。未接线（无 UI 环境）时跳过对话框按默认 PNG 直接走保存流程。internal 供单测注入替身。
    /// </summary>
    internal Func<ImageHistoryNode, ExportOptions?, Task<ExportChoice?>>? ExportDialogProvider;

    private async Task SaveToPickerAsync(ImageHistoryNode node)
    {
        if (_storage is null) return;
        // 对话框先行（预估与编码都在其中完成），取消则不弹文件选择器
        var choice = ExportDialogProvider is { } provider
            ? await provider(node, _lastExportOptions)
            : null;
        if (choice is null) return;
        var format = choice.Format;
        _lastExportOptions = new ExportOptions(format, choice.Quality);
        var extension = ExtensionFor(format);
        // 起始位置与重名探测用同一目录：上次保存目录优先，退回源文件目录
        var dir = _lastSaveDirectory ?? _sourceDirectory;
        var file = await _storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Translations.Instance.PickerSaveTitle,
            SuggestedStartLocation = await TryGetFolderAsync(dir),
            SuggestedFileName = SuggestFileName(node, extension),
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [FileTypeFor(format)],
        });
        if (file is null) return;
        if (file.Path.IsFile)
            _lastSaveDirectory = GetDirectoryNameOrNull(file.Path.LocalPath);
        try
        {
            // 编码字节在对话框里已备好，这里只落盘（大数组 WriteAsync 走异步 I/O，不冻结 UI）
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0); // OpenWriteAsync 不截断，覆盖更长的旧文件会留垃圾尾
            await stream.WriteAsync(choice.Bytes);
            StatusText = string.Format(Translations.Instance.Saved, file.Name);
        }
        catch (Exception e)
        {
            StatusText = string.Format(Translations.Instance.SaveFailed, e.Message);
        }
    }

    /// <summary>导出格式的标准扩展名（JPEG 用 .jpg：三大平台文件选择器兼容性最好）。internal 供单测。</summary>
    internal static string ExtensionFor(ExportFormat format) => format switch
    {
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.WebP => ".webp",
        _ => ".png",
    };

    private static FilePickerFileType FileTypeFor(ExportFormat format) => format switch
    {
        ExportFormat.Jpeg => new FilePickerFileType(Translations.Instance.JpegFileType) { Patterns = ["*.jpg", "*.jpeg"] },
        ExportFormat.WebP => new FilePickerFileType(Translations.Instance.WebPFileType) { Patterns = ["*.webp"] },
        _ => new FilePickerFileType(Translations.Instance.PngFileType) { Patterns = ["*.png"] },
    };

    /// <summary>把目录路径转成选择器起始位置；目录不存在或转换失败时返回 null（不影响保存）。</summary>
    private async Task<IStorageFolder?> TryGetFolderAsync(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        try
        {
            return await _storage!.TryGetFolderFromPathAsync(new Uri(dir));
        }
        catch
        {
            return null;
        }
    }

    private static string? GetDirectoryNameOrNull(string path)
    {
        var dir = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(dir) ? null : dir;
    }

    /// <summary>
    /// 保存对话框的建议文件名：原图节点直接用源文件名，生成节点在其后追加历史标题作后缀
    /// （清理路径非法字符）；没有源文件名时原图退回时间戳兜底，保证对话框不为空。
    /// extension 随导出格式变化，重名探测按它比对。internal 供单测。
    /// </summary>
    internal string SuggestFileName(ImageHistoryNode node, string extension = ".png")
    {
        string? suffix = node.Parent is null ? null : SanitizeFileName(node.Title);
        bool hasSuffix = !string.IsNullOrEmpty(suffix);
        string baseName;
        if (string.IsNullOrEmpty(_sourceFileName))
            baseName = hasSuffix ? suffix! : $"Inpaint_{DateTime.Now:yyyyMMdd-HHmmss}";
        else
            baseName = hasSuffix ? $"{_sourceFileName}_{suffix}" : _sourceFileName;
        return FindFreeFileName(baseName, extension);
    }

    /// <summary>
    /// 已知目录（上次保存目录优先，退回源文件目录）里探测重名并自动递增 (2) (3)…，
    /// 避免反复触发系统的覆盖确认；目录未知则原样返回，由覆盖确认兜底。
    /// </summary>
    private string FindFreeFileName(string baseName, string extension)
    {
        var dir = _lastSaveDirectory ?? _sourceDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return baseName;
        if (!File.Exists(Path.Combine(dir, baseName + extension))) return baseName;
        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName}({i})";
            if (!File.Exists(Path.Combine(dir, candidate + extension))) return candidate;
        }
    }

    /// <summary>去掉文件名非法字符与空白，供拼接建议文件名使用。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalid.Contains(c) && !char.IsWhiteSpace(c)).ToArray());
    }

    private IProgress<double> DownloadProgress() => new Progress<double>(p =>
    {
        Progress = p;
        StatusText = string.Format(Translations.Instance.DownloadingModel, p);
    });

    private IProgress<double> TileProgress() => new Progress<double>(p => Progress = p);

    /// <summary>Bgra8888 写入 WriteableBitmap（逐行处理 stride）。internal 供单测。</summary>
    internal static WriteableBitmap CreateBitmap(PixelSize size, byte[] bgra)
    {
        var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormats.Bgra8888);
        using var frame = bitmap.Lock();
        unsafe
        {
            var dst = (byte*)frame.Address;
            fixed (byte* src = bgra)
            {
                if (frame.RowBytes == 4 * size.Width)
                {
                    Buffer.MemoryCopy(src, dst, (long)frame.RowBytes * size.Height, bgra.Length);
                }
                else
                {
                    for (int y = 0; y < size.Height; y++)
                        Buffer.MemoryCopy(
                            src + 4L * size.Width * y,
                            dst + (long)frame.RowBytes * y,
                            4L * size.Width, 4L * size.Width);
                }
            }
        }
        return bitmap;
    }
}
