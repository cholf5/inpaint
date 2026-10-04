using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Inpaint.App.Services;
using Inpaint.App.ViewModels;

namespace Inpaint.Tests;

/// <summary>
/// 导出编码语义：三种格式的字节头与可回读性、JPEG 的 alpha 拍扁到白底、PNG/WebP 的 alpha 保留。
/// 依赖真实 Skia 编码器，走 headless（TestAppBuilder 的 UseSkia）。
/// </summary>
public class ImageExporterTests
{
    private static WriteableBitmap MakeBitmap(int width, int height)
    {
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888);
        using var frame = bmp.Lock();
        Marshal.Copy(new byte[frame.RowBytes * height], 0, frame.Address, frame.RowBytes * height);
        return bmp;
    }

    /// <summary>把 BGRA 字节写入 1×1 位图（premul 语义，与 Avalonia 解码结果一致）。</summary>
    private static WriteableBitmap MakePixel(byte b, byte g, byte r, byte a)
    {
        var bmp = MakeBitmap(1, 1);
        using var frame = bmp.Lock();
        Marshal.Copy(new[] { b, g, r, a }, 0, frame.Address, 4);
        return bmp;
    }

    /// <summary>解码导出字节并读回首个像素的 BGRA。</summary>
    private static byte[] DecodeFirstPixel(byte[] encoded)
    {
        using var ms = new MemoryStream(encoded);
        var bitmap = new Bitmap(ms);
        Assert.Equal(1, bitmap.PixelSize.Width);
        Assert.Equal(1, bitmap.PixelSize.Height);
        return ImageExporter.ExtractBgra(bitmap);
    }

    [AvaloniaFact]
    public void CreateBitmap_ExtractBgra_往返一致()
    {
        var bgra = new byte[4 * 3 * 2];
        for (int i = 0; i < bgra.Length; i++)
            bgra[i] = (byte)(i * 37 % 256);

        var bitmap = MainWindowViewModel.CreateBitmap(new PixelSize(3, 2), bgra);

        Assert.Equal(bgra, ImageExporter.ExtractBgra(bitmap));
    }

    [AvaloniaFact]
    public void ExtractBgra_Rgba8888源交换红蓝()
    {
        var rgba = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormats.Rgba8888);
        using (var frame = rgba.Lock())
            Marshal.Copy(new byte[] { 1, 2, 3, 255 }, 0, frame.Address, 4);

        // Rgba8888 → Bgra8888：R/B 对调，G/A 不变
        Assert.Equal(new byte[] { 3, 2, 1, 255 }, ImageExporter.ExtractBgra(rgba));
    }

    [AvaloniaTheory]
    [InlineData(ExportFormat.Png, new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(ExportFormat.Jpeg, new byte[] { 0xFF, 0xD8 })]
    [InlineData(ExportFormat.WebP, new byte[] { 0x52, 0x49, 0x46, 0x46 })] // RIFF 容器
    public void Encode_输出字节头与格式匹配且可回读(ExportFormat format, byte[] magic)
    {
        using var bitmap = MakeBitmap(4, 3);

        var encoded = ImageExporter.Encode(bitmap, format, quality: 80);

        Assert.True(encoded.Length > 0);
        Assert.Equal(magic, encoded.Take(magic.Length));
        // 回读尺寸一致
        using var ms = new MemoryStream(encoded);
        using var decoded = new Bitmap(ms);
        Assert.Equal(new PixelSize(4, 3), decoded.PixelSize);
    }

    [AvaloniaFact]
    public void Encode_Jpeg_半透明像素合成到白底()
    {
        // 纯红直通色 (255,0,0) 的半透明像素，预乘后 = R 128 / A 128（Avalonia 缓冲为预乘）
        using var bitmap = MakePixel(b: 0, g: 0, r: 128, a: 128);

        var encoded = ImageExporter.Encode(bitmap, ExportFormat.Jpeg, quality: 95);

        // 白底合成（c_premul + 255 - a）：半透明红叠白底 = 粉色 (255, 127, 127)，alpha 归不透明
        var pixel = DecodeFirstPixel(encoded);
        Assert.True(pixel[3] == 255, $"JPEG 输出应不透明，实际 A={pixel[3]}");
        Assert.True(pixel[2] > 240, $"R 通道应为满值，实际 R={pixel[2]}");
        Assert.True(Math.Abs(pixel[1] - 127) < 10 && Math.Abs(pixel[0] - 127) < 10,
            $"半透明红叠白底应为粉色，实际 G={pixel[1]} B={pixel[0]}");
    }

    [AvaloniaFact]
    public void Encode_Jpeg_全不透明像素直通()
    {
        using var bitmap = MakePixel(b: 0, g: 0, r: 255, a: 255);

        var encoded = ImageExporter.Encode(bitmap, ExportFormat.Jpeg, quality: 95);

        var pixel = DecodeFirstPixel(encoded);
        Assert.True(pixel[3] == 255);
        Assert.True(pixel[2] > 240 && pixel[1] < 15 && pixel[0] < 15,
            $"不透明红经 JPEG 后应保持纯红，实际 RGB=({pixel[2]},{pixel[1]},{pixel[0]})");
    }

    [AvaloniaFact]
    public void Encode_Png_保留半透明()
    {
        using var bitmap = MakePixel(b: 0, g: 0, r: 128, a: 128);

        var encoded = ImageExporter.Encode(bitmap, ExportFormat.Png, quality: 100);

        // PNG 无损：解码回 Avalonia 后仍是预乘的 (128, 0, 0, 128)，色值不因导出漂移
        var pixel = DecodeFirstPixel(encoded);
        Assert.Equal((byte)128, pixel[3]);
        Assert.Equal((byte)128, pixel[2]);
        Assert.Equal((byte)0, pixel[1]);
        Assert.Equal((byte)0, pixel[0]);
    }

    [AvaloniaFact]
    public void Encode_质量钳制到合法区间()
    {
        using var bitmap = MakeBitmap(2, 2);

        Assert.True(ImageExporter.Encode(bitmap, ExportFormat.Jpeg, quality: 0).Length > 0);
        Assert.True(ImageExporter.Encode(bitmap, ExportFormat.WebP, quality: 999).Length > 0);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData((long)(1.5 * 1024 * 1024), "1.5 MB")]
    public void FormatBytes_人读格式(long bytes, string expected)
    {
        Assert.Equal(expected, ImageExporter.FormatBytes(bytes));
    }
}
