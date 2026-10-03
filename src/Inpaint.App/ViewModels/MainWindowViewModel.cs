using Avalonia;
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
    private const int MaxHistory = 25;

    private static readonly FilePickerFileType ImageFileTypes = new("图片")
    {
        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
    };

    private readonly IStorageProvider? _storage;
    private readonly List<Bitmap> _history = [];
    private int _historyIndex = -1;
    private InpaintEngine? _inpaintEngine;
    private UpscaleEngine? _upscaleEngine;

    [ObservableProperty] private Bitmap? _currentImage;
    [ObservableProperty] private WriteableBitmap? _maskImage;
    [ObservableProperty] private double _brushSize = 40;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "打开一张图片，涂抹掉不想要的内容";
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
    private bool _isBusy;

    public MainWindowViewModel(IStorageProvider? storage) => _storage = storage;

    private bool NotBusy() => !IsBusy;
    private bool NotBusyAndHasImage() => !IsBusy && HasImage;
    private bool CanUndo() => !IsBusy && _historyIndex > 0;

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

    /// <summary>接管一张位图作为新的编辑起点（清空历史与涂抹）。</summary>
    public void AdoptBitmap(Bitmap bitmap)
    {
        foreach (var old in _history) old.Dispose();
        _history.Clear();
        _history.Add(bitmap);
        _historyIndex = 0;
        CurrentImage = bitmap;
        MaskImage = CreateMask(bitmap.PixelSize);
        HasImage = true;
        StatusText = $"已加载 {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}，涂抹后点「修复涂抹区域」";
    }

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
            PushHistory(CreateBitmap(size, ImageProcessing.RgbChwToBgra(output, size.Width, size.Height)));
            MaskImage = CreateMask(size);
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
            PushHistory(CreateBitmap(newSize, ImageProcessing.RgbChwF32ToBgra(output, newSize.Width, newSize.Height)));
            MaskImage = CreateMask(newSize);
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

    // ---- 历史 / 遮罩 / 保存 ----

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_historyIndex <= 0) return;
        _historyIndex--;
        var bitmap = _history[_historyIndex];
        CurrentImage = bitmap;
        MaskImage = CreateMask(bitmap.PixelSize);
        StatusText = "已撤销";
        UndoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private void Reset()
    {
        if (_history.Count == 0) return;
        var original = _history[0];
        _history.Clear();
        _history.Add(original);
        _historyIndex = 0;
        CurrentImage = original;
        MaskImage = CreateMask(original.PixelSize);
        StatusText = "已回到原图";
        UndoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private void ClearMask()
    {
        if (CurrentImage is { } bitmap) MaskImage = CreateMask(bitmap.PixelSize);
        StatusText = "已清除涂抹";
    }

    [RelayCommand(CanExecute = nameof(NotBusyAndHasImage))]
    private async Task SaveAsync()
    {
        if (CurrentImage is not { } bitmap || _storage is null) return;
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

    // ---- 内部工具 ----

    /// <summary>压入新历史并截断重做分支。internal 供单测。</summary>
    internal void PushHistory(Bitmap bitmap)
    {
        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(bitmap);
        if (_history.Count > MaxHistory) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
        CurrentImage = bitmap;
        UndoCommand.NotifyCanExecuteChanged();
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
