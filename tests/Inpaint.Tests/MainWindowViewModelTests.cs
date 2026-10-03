using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Inpaint.App.ViewModels;

namespace Inpaint.Tests;

/// <summary>
/// ViewModel 位图生命周期与命令可用性。Bitmap 相关操作需要 Avalonia 平台，走 headless。
/// </summary>
public class MainWindowViewModelTests
{
    private static WriteableBitmap MakeBitmap(int width, int height)
    {
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888);
        using var frame = bmp.Lock();
        Marshal.Copy(new byte[frame.RowBytes * height], 0, frame.Address, frame.RowBytes * height);
        return bmp;
    }

    private static byte[] ReadMaskPixels(WriteableBitmap mask, int width, int height)
    {
        using var frame = mask.Lock();
        var raw = new byte[frame.RowBytes * height];
        Marshal.Copy(frame.Address, raw, 0, raw.Length);
        // 逐行去掉 RowBytes stride，得到紧凑 BGRA
        var compact = new byte[4L * width * height];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(raw, y * frame.RowBytes, compact, y * 4 * width, 4 * width);
        return compact;
    }

    [AvaloniaFact]
    public void AdoptBitmap_初始化图片遮罩与命令可用性()
    {
        var vm = new MainWindowViewModel(null);
        Assert.False(vm.HasImage);
        Assert.False(vm.InpaintCommand.CanExecute(null));

        vm.AdoptBitmap(MakeBitmap(6, 4));

        Assert.True(vm.HasImage);
        Assert.Equal(new PixelSize(6, 4), vm.CurrentImage!.PixelSize);
        Assert.Equal(new PixelSize(6, 4), vm.MaskImage!.PixelSize);
        // 回归：HasImage 变化必须通知命令，否则按钮在加载后仍不可用
        Assert.True(vm.InpaintCommand.CanExecute(null));
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.UpscaleCommand.CanExecute(null));
        Assert.True(vm.ResetCommand.CanExecute(null));
        Assert.True(vm.ClearMaskCommand.CanExecute(null));
        Assert.False(vm.UndoCommand.CanExecute(null));
        // 遮罩初始全 0 字节（经 MaskBgraToChw 全部映射为 255 = 保留）
        Assert.All(ReadMaskPixels(vm.MaskImage, 6, 4), b => Assert.Equal(0, (int)b));
    }

    [AvaloniaFact]
    public async Task LoadFromStreamAsync_无效流写入StatusText不抛异常()
    {
        var vm = new MainWindowViewModel(null);

        await vm.LoadFromStreamAsync(new MemoryStream([1, 2, 3]));

        Assert.StartsWith("无法打开图片", vm.StatusText);
        Assert.False(vm.HasImage);
        Assert.False(vm.InpaintCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void PushHistory_撤销生效且重做分支被截断()
    {
        var vm = new MainWindowViewModel(null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        vm.PushHistory(MakeBitmap(2, 2));
        vm.PushHistory(MakeBitmap(3, 3));

        Assert.Equal(new PixelSize(3, 3), vm.CurrentImage!.PixelSize);
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.Equal(new PixelSize(2, 2), vm.CurrentImage!.PixelSize);

        // 撤销后推入新位图：3×3 所在的重做分支必须被丢弃，但撤销点 2×2 保留
        vm.PushHistory(MakeBitmap(4, 4));
        vm.UndoCommand.Execute(null);
        Assert.Equal(new PixelSize(2, 2), vm.CurrentImage!.PixelSize);
        vm.UndoCommand.Execute(null);
        Assert.Equal(new PixelSize(1, 1), vm.CurrentImage!.PixelSize);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void PushHistory_历史上限25步()
    {
        var vm = new MainWindowViewModel(null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        // 共 31 条历史：#1(1×1) 载入 + #2..#31(2..31 尺寸) 推入
        for (int i = 2; i <= 31; i++)
            vm.PushHistory(MakeBitmap(i, i));

        for (int i = 0; i < 24; i++)
        {
            Assert.True(vm.UndoCommand.CanExecute(null), $"第 {i + 1} 次撤销应可用");
            vm.UndoCommand.Execute(null);
        }

        // 上限 25 条：最早的 #1..#6 被丢弃，24 次撤销后停在 #7(7×7) 且不可再撤销
        Assert.Equal(new PixelSize(7, 7), vm.CurrentImage!.PixelSize);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Reset_回到原图()
    {
        var vm = new MainWindowViewModel(null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        vm.PushHistory(MakeBitmap(2, 2));
        vm.PushHistory(MakeBitmap(3, 3));

        vm.ResetCommand.Execute(null);

        Assert.Equal(new PixelSize(1, 1), vm.CurrentImage!.PixelSize);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void CreateBitmap_ExtractBgra_往返一致()
    {
        var bgra = new byte[4 * 3 * 2];
        for (int i = 0; i < bgra.Length; i++)
            bgra[i] = (byte)(i * 37 % 256);

        var bitmap = MainWindowViewModel.CreateBitmap(new PixelSize(3, 2), bgra);

        Assert.Equal(bgra, MainWindowViewModel.ExtractBgra(bitmap));
    }

    [AvaloniaFact]
    public void ExtractBgra_Rgba8888源交换红蓝()
    {
        var rgba = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormats.Rgba8888);
        using (var frame = rgba.Lock())
            Marshal.Copy(new byte[] { 1, 2, 3, 255 }, 0, frame.Address, 4);

        // Rgba8888 → Bgra8888：R/B 对调，G/A 不变
        Assert.Equal(new byte[] { 3, 2, 1, 255 }, MainWindowViewModel.ExtractBgra(rgba));
    }
}
