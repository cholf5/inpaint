using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Inpaint.Core;
using Inpaint.Inference;

namespace Inpaint.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>历史节点总数上限。超限时优先丢弃最旧的非当前分支；原图（根）永不丢弃。</summary>
    private const int MaxHistory = 25;

    private const int ThumbnailMaxSide = 96;

    /// <summary>画笔大小范围（与 MainWindow 滑块一致）。internal 供单测。</summary>
    internal const double MinBrushSize = 4;
    internal const double MaxBrushSize = 160;
    private const double BrushSizeStep = 10;

    private static readonly FilePickerFileType ImageFileTypes = new("图片")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
    };

    private readonly IStorageProvider? _storage;
    private readonly IClipboard? _clipboard;
    private ImageHistoryNode? _root;
    private InpaintEngine? _inpaintEngine;
    private UpscaleEngine? _upscaleEngine;

    [ObservableProperty] private Bitmap? _currentImage;
    [ObservableProperty] private WriteableBitmap? _maskImage;
    [ObservableProperty] private double _brushSize = 40;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "打开一张图片，涂抹掉不想要的内容";
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
    private bool _isBusy;

    public MainWindowViewModel(IStorageProvider? storage, IClipboard? clipboard)
    {
        _storage = storage;
        _clipboard = clipboard;
    }

    private bool NotBusy() => !IsBusy;
    private bool NotBusyAndHasImage() => !IsBusy && HasImage;
    private bool CanUndo() => !IsBusy && CurrentNode?.Parent is not null;
    private bool CanOperateNode(ImageHistoryNode? node) => !IsBusy && node is not null && _clipboard is not null;

    // ---- 图片载入 ----

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task OpenAsync()
    {
        if (_storage is null) return;
        var files = await _storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开图片",
            AllowMultiple = false,
            FileTypeFilter = [ImageFileTypes],
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        await LoadFromStreamAsync(stream);
    }

    public async Task LoadFromPathAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        await LoadFromStreamAsync(stream);
    }

    public async Task LoadFromStreamAsync(Stream stream)
    {
        try
        {
            AdoptBitmap(new Bitmap(stream));
        }
        catch (Exception e)
        {
            StatusText = "无法打开图片：" + e.Message;
        }
        await Task.CompletedTask;
    }

    /// <summary>接管一张位图作为新的编辑起点（开一棵新的历史树，旧树整体释放）。</summary>
    public void AdoptBitmap(Bitmap bitmap)
    {
        if (_root is not null) DisposeTree(_root);
        var node = new ImageHistoryNode(bitmap, "原图", null) { Thumbnail = CreateThumbnail(bitmap) };
        _root = node;
        CurrentNode = node;
        RebuildHistory();
        CurrentImage = bitmap;
        SetMask(bitmap.PixelSize);
        HasImage = true;
        StatusText = $"已加载 {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}，涂抹后点「修复涂抹区域」";
        UndoCommand.NotifyCanExecuteChanged();
    }

    // ---- 画笔大小（滑块 / 快捷键 [ ]）----

    /// <summary>快捷键 [：缩小画笔，钳制到滑块最小值。</summary>
    [RelayCommand]
    private void DecreaseBrushSize() => BrushSize = Math.Max(MinBrushSize, BrushSize - BrushSizeStep);

    /// <summary>快捷键 ]：放大画笔，钳制到滑块最大值。</summary>
    [RelayCommand]
    private void IncreaseBrushSize() => BrushSize = Math.Min(MaxBrushSize, BrushSize + BrushSizeStep);

    // ---- 修复 ----

    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private async Task InpaintAsync()
    {
        if (CurrentImage is not { } current || MaskImage is not { } mask) return;
        IsBusy = true;
        try
        {
            var size = current.PixelSize;
            Progress = 0;
            StatusText = "正在准备修复模型…";
            _inpaintEngine ??= await InpaintEngine.CreateAsync(DownloadProgress());
            StatusText = "正在修复…";
            var imageChw = ImageProcessing.BgraToRgbChw(ExtractBgra(current), size.Width, size.Height);
            var maskChw = ImageProcessing.MaskBgraToChw(ExtractBgra(mask), size.Width, size.Height);
            var output = await Task.Run(() => _inpaintEngine.Run(size.Width, size.Height, imageChw, maskChw));
            PushHistory(CreateBitmap(size, ImageProcessing.RgbChwToBgra(output, size.Width, size.Height)), "修复");
            SetMask(size);
            StatusText = $"修复完成（{size.Width}×{size.Height}）";
        }
        catch (Exception e)
        {
            StatusText = "修复失败：" + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- 高清放大 ----

    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private async Task UpscaleAsync()
    {
        if (CurrentImage is not { } current) return;
        IsBusy = true;
        try
        {
            var size = current.PixelSize;
            Progress = 0;
            StatusText = "正在准备超分模型…";
            _upscaleEngine ??= await UpscaleEngine.CreateAsync(DownloadProgress());
            StatusText = $"正在放大 {size.Width}×{size.Height} → {size.Width * 4}×{size.Height * 4}…";
            var chw = ImageProcessing.BgraToRgbChwF32(ExtractBgra(current), size.Width, size.Height);
            var output = await Task.Run(
                () => _upscaleEngine.Run(size.Width, size.Height, chw, TileProgress()));
            var newSize = new PixelSize(size.Width * 4, size.Height * 4);
            PushHistory(CreateBitmap(newSize, ImageProcessing.RgbChwF32ToBgra(output, newSize.Width, newSize.Height)), "放大 ×4");
            SetMask(newSize);
            StatusText = $"放大完成（{newSize.Width}×{newSize.Height}）";
        }
        catch (Exception e)
        {
            StatusText = "放大失败：" + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- 生成历史 / 遮罩 / 复制 / 保存 ----

    /// <summary>点击节点：预览该图并将其作为后续生成的新基准；基于中间节点继续生成即开新车道分叉。</summary>
    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void SelectNode(ImageHistoryNode? node)
    {
        if (node is null || node == CurrentNode) return;
        SwitchTo(node, $"已切换到「{node.Title}」，继续修复/放大将开新车道分叉");
    }

    /// <summary>撤销 = 在历史树上移动到父节点。</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (CurrentNode?.Parent is not { } parent) return;
        SwitchTo(parent, "已撤销");
    }

    /// <summary>回到原图 = 移动到根节点；其余分支保留，可随时点击切回。</summary>
    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private void Reset()
    {
        if (_root is null || _root == CurrentNode) return;
        SwitchTo(_root, "已回到原图");
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
            StatusText = $"已复制「{node.Title}」到剪贴板";
        }
        catch (Exception e)
        {
            StatusText = "复制失败：" + e.Message;
        }
    }

    /// <summary>右键菜单：把任意历史图另存为 PNG。</summary>
    [RelayCommand(CanExecute = nameof(CanOperateNode))]
    private async Task SaveNodeAsync(ImageHistoryNode? node)
    {
        if (node?.Image is not { } bitmap) return;
        await SaveToPickerAsync(bitmap);
    }

    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private void ClearMask()
    {
        if (CurrentImage is { } bitmap) SetMask(bitmap.PixelSize);
        StatusText = "已清除涂抹";
    }

    /// <summary>工具栏「保存 PNG」：保存当前预览图。</summary>
    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private async Task SaveAsync()
    {
        if (CurrentImage is not { } bitmap) return;
        await SaveToPickerAsync(bitmap);
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
    /// 节点总数超上限时从最旧开始丢弃不在当前路径上的节点（其子树随之整体删除，
    /// 子节点向上拼接到祖父以保持其余分支连通）；若整棵树是一条链则退而拼接最旧的非根节点。
    /// 原图（根）与当前预览节点永不丢弃。
    /// </summary>
    private void PruneHistory()
    {
        if (_root is null) return;
        bool removed = false;
        while (AllNodes(_root).Count() > MaxHistory)
        {
            var path = CurrentPathSet();
            var victim = AllNodes(_root)
                    .Where(n => n != _root && !path.Contains(n))
                    .MinBy(n => n.Id)
                ?? AllNodes(_root)
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
        MaskImage?.Dispose();
        MaskImage = CreateMask(size);
    }

    private async Task SaveToPickerAsync(Bitmap bitmap)
    {
        if (_storage is null) return;
        var file = await _storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存图片",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }],
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            bitmap.Save(stream);
            StatusText = "已保存：" + file.Name;
        }
        catch (Exception e)
        {
            StatusText = "保存失败：" + e.Message;
        }
    }

    private IProgress<double> DownloadProgress() => new Progress<double>(p =>
    {
        Progress = p;
        StatusText = $"正在下载模型 {p:F0}%（首次使用需下载，之后有本地缓存）";
    });

    private IProgress<double> TileProgress() => new Progress<double>(p => Progress = p);

    private static WriteableBitmap CreateMask(PixelSize size)
    {
        var mask = new WriteableBitmap(size, new Vector(96, 96), PixelFormats.Bgra8888);
        using var frame = mask.Lock();
        unsafe
        {
            new Span<byte>((void*)frame.Address, frame.RowBytes * size.Height).Clear();
        }
        return mask;
    }

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

    /// <summary>读出紧凑 BGRA 字节；Rgba8888 源交换红蓝。internal 供单测。</summary>
    internal static byte[] ExtractBgra(Bitmap bitmap)
    {
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        int stride = 4 * width;
        var result = new byte[4L * width * height];
        bool swap = bitmap.Format is { } format && format == PixelFormats.Rgba8888;
        nint buffer = Marshal.AllocHGlobal(result.Length);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), buffer, result.Length, stride);
            unsafe
            {
                var src = (byte*)buffer;
                fixed (byte* dst = result)
                {
                    if (!swap)
                    {
                        Buffer.MemoryCopy(src, dst, result.Length, result.Length);
                    }
                    else
                    {
                        for (int i = 0; i < result.Length; i += 4)
                        {
                            dst[i] = src[i + 2];
                            dst[i + 1] = src[i + 1];
                            dst[i + 2] = src[i];
                            dst[i + 3] = src[i + 3];
                        }
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }
}
