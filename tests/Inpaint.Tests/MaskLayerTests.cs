using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Inpaint.App.Controls;

namespace Inpaint.Tests;

/// <summary>
/// 遮罩层两层语义：权威 Data（每像素 1 字节，255 = 待修复）与低分辨率显示 Overlay 同步写入，
/// Clear 同时复位两层。像素断言只打权威 Data；overlay 走 WriteableBitmap.Lock 读原始字节。
/// </summary>
public class MaskLayerTests
{
    private static byte[] ReadOverlayPixels(MaskLayer mask)
    {
        using var frame = mask.Overlay.Lock();
        var raw = new byte[frame.RowBytes * mask.Overlay.PixelSize.Height];
        Marshal.Copy(frame.Address, raw, 0, raw.Length);
        return raw;
    }

    private static bool OverlayPixelIsWhite(MaskLayer mask, int x, int y)
    {
        var raw = ReadOverlayPixels(mask);
        int stride = mask.Overlay.PixelSize.Width * 4;
        int i = y * stride + x * 4;
        return raw[i] == 255 && raw[i + 1] == 255 && raw[i + 2] == 255 && raw[i + 3] == 255;
    }

    [AvaloniaFact]
    public void 构造_权威数据与overlay清空()
    {
        using var mask = new MaskLayer(new PixelSize(64, 48));

        Assert.Equal(new PixelSize(64, 48), mask.ImageSize);
        Assert.Equal(64L * 48, mask.Data.Length);
        Assert.All(mask.Data, b => Assert.Equal(0, b));
        // overlay 长边 ≤ 2048：64×48 原样保留
        Assert.Equal(new PixelSize(64, 48), mask.Overlay.PixelSize);
        Assert.All(ReadOverlayPixels(mask), b => Assert.Equal(0, b));
    }

    [AvaloniaFact]
    public void Overlay尺寸_超大图长边钳制到2048_保持宽高比()
    {
        using var mask = new MaskLayer(new PixelSize(8000, 4000));

        Assert.Equal(new PixelSize(2048, 1024), mask.Overlay.PixelSize);
    }

    [AvaloniaFact]
    public void Overlay尺寸_小于上限的图不放大()
    {
        using var mask = new MaskLayer(new PixelSize(100, 50));

        Assert.Equal(new PixelSize(100, 50), mask.Overlay.PixelSize);
    }

    [AvaloniaFact]
    public void PaintDisc_权威数据与overlay同步写入圆盘()
    {
        using var mask = new MaskLayer(new PixelSize(64, 48));

        mask.PaintDisc(32, 24, 4);

        // 权威数据：圆心、边界（dx²=r² 不排除）在内，圆外保留
        Assert.Equal(255, mask.Data[24 * 64 + 32]);
        Assert.Equal(255, mask.Data[24 * 64 + 36]);
        Assert.Equal(0, mask.Data[24 * 64 + 37]);
        Assert.Equal(0, mask.Data[0]);
        // overlay 与图片同分辨率（≤2048）时坐标一一对应
        Assert.True(OverlayPixelIsWhite(mask, 32, 24));
        Assert.True(OverlayPixelIsWhite(mask, 36, 24));
        Assert.False(OverlayPixelIsWhite(mask, 37, 24));
    }

    [AvaloniaFact]
    public void PaintDisc_越界部分钳掉_不抛异常()
    {
        using var mask = new MaskLayer(new PixelSize(64, 48));

        mask.PaintDisc(1, 1, 4);
        mask.PaintDisc(63, 47, 4);
        mask.PaintDisc(-10, -10, 4);

        Assert.Equal(255, mask.Data[0]);
        Assert.Equal(255, mask.Data[47 * 64 + 63]);
    }

    [AvaloniaFact]
    public void PaintDisc_半径非正_无效果()
    {
        using var mask = new MaskLayer(new PixelSize(8, 8));

        mask.PaintDisc(4, 4, 0);
        mask.PaintDisc(4, 4, -2);

        Assert.All(mask.Data, b => Assert.Equal(0, b));
    }

    [AvaloniaFact]
    public void Clear_两层同时复位()
    {
        using var mask = new MaskLayer(new PixelSize(64, 48));
        mask.PaintDisc(32, 24, 4);

        mask.Clear();

        Assert.All(mask.Data, b => Assert.Equal(0, b));
        Assert.All(ReadOverlayPixels(mask), b => Assert.Equal(0, b));
    }
}
