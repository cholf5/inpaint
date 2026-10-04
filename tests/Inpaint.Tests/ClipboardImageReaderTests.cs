using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Inpaint.App.Services;

namespace Inpaint.Tests;

/// <summary>
/// 剪贴板图片字节兜底读取：格式白名单探测 + 解码分发（TIFF 仅 macOS 经 ImageIO）。
/// 格式探测不碰位图走普通 Fact；解码需要 Skia，走 headless。
/// </summary>
public class ClipboardImageReaderTests
{
    private static DataFormat<byte[]> Bytes(string identifier) => DataFormat.CreateBytesPlatformFormat(identifier);

    // ---- 格式探测（纯逻辑，不需要平台） ----

    [Fact]
    public async Task TryGetImageBytes_命中白名单返回字节()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var dto = new FakeAsyncDataTransfer(
            new FakeAsyncDataTransferItem((Bytes("public.tiff"), bytes)));

        var result = await ClipboardImageReader.TryGetImageBytesAsync(dto);

        Assert.NotNull(result);
        Assert.Same(bytes, result!.Value.Bytes);
        Assert.True(result.Value.IsTiff);
    }

    [Fact]
    public async Task TryGetImageBytes_Mime格式走Skia通道()
    {
        var dto = new FakeAsyncDataTransfer(
            new FakeAsyncDataTransferItem((Bytes("image/jpeg"), new byte[] { 9 })));

        var result = await ClipboardImageReader.TryGetImageBytesAsync(dto);

        Assert.NotNull(result);
        Assert.False(result!.Value.IsTiff);
    }

    [Fact]
    public async Task TryGetImageBytes_白名单外与文本位图格式忽略()
    {
        var dto = new FakeAsyncDataTransfer(
            new FakeAsyncDataTransferItem(
                (DataFormat.Text, "你好"),
                (DataFormat.Bitmap, null),
                (Bytes("public.html"), new byte[] { 1 }),
                (Bytes("net.avaloniaui.app.uti.unknown"), new byte[] { 2 })));

        Assert.Null(await ClipboardImageReader.TryGetImageBytesAsync(dto));
    }

    [Fact]
    public async Task TryGetImageBytes_声明了格式但取值失败继续尝试下一个()
    {
        var dto = new FakeAsyncDataTransfer(
            new FakeAsyncDataTransferItem(
                (Bytes("public.tiff"), null),
                (Bytes("image/png"), new byte[] { 7 })));

        var result = await ClipboardImageReader.TryGetImageBytesAsync(dto);

        Assert.NotNull(result);
        Assert.False(result!.Value.IsTiff);
    }

    // ---- 解码（需要 Skia 平台） ----

    [AvaloniaFact]
    public void DecodeImageBytes_PNG字节解码()
    {
        var png = MakePngBytes(3, 2);

        using var decoded = ClipboardImageReader.DecodeImageBytes(png, isTiff: false);

        Assert.NotNull(decoded);
        Assert.Equal(new PixelSize(3, 2), decoded!.PixelSize);
    }

    [AvaloniaFact]
    public void DecodeImageBytes_垃圾字节返回Null不抛()
    {
        Assert.Null(ClipboardImageReader.DecodeImageBytes([1, 2, 3, 4], isTiff: false));
    }

    [AvaloniaFact]
    public void DecodeImageBytes_合成TIFF像素逐点核对()
    {
        if (!OperatingSystem.IsMacOS()) return; // ImageIO 只在 macOS 上存在

        // 2×2：上排 红、绿；下排 白、黑（RGB 行序）
        byte[] rgb = [255, 0, 0, 0, 255, 0, 255, 255, 255, 0, 0, 0];
        using var decoded = ClipboardImageReader.DecodeImageBytes(MakeTiffBytes(2, 2, rgb), isTiff: true);

        Assert.NotNull(decoded);
        Assert.Equal(new PixelSize(2, 2), decoded!.PixelSize);
        // 转换器直接构造 Bgra8888（预乘）位图，不经过重编码，像素可精确核对
        var stride = 2 * 4;
        var buffer = Marshal.AllocHGlobal(stride * 2);
        try
        {
            decoded.CopyPixels(new PixelRect(0, 0, 2, 2), buffer, stride * 2, stride);
            var pixels = new byte[stride * 2];
            Marshal.Copy(buffer, pixels, 0, pixels.Length);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.Take(4).ToArray()); // 红
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels.Skip(4).Take(4).ToArray()); // 绿
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, pixels.Skip(8).Take(4).ToArray()); // 白
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, pixels.Skip(12).Take(4).ToArray()); // 黑
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [AvaloniaFact]
    public void DecodeImageBytes_非Mac平台TIFF返回Null()
    {
        if (OperatingSystem.IsMacOS()) return;

        using var decoded = ClipboardImageReader.DecodeImageBytes(MakeTiffBytes(1, 1, [255, 0, 0]), isTiff: true);
        Assert.Null(decoded);
    }

    /// <summary>生成一张纯色 PNG 字节（经 Skia 编码）。</summary>
    private static byte[] MakePngBytes(int width, int height)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        using (var ctx = bitmap.CreateDrawingContext())
            ctx.FillRectangle(Brushes.CadetBlue, new Rect(0, 0, width, height));
        using var ms = new MemoryStream();
        bitmap.Save(ms, PngBitmapEncoderOptions.Default);
        return ms.ToArray();
    }

    /// <summary>合成最小未压缩 baseline TIFF（小端，RGB 分块布局），供 ImageIO 真实解码。</summary>
    private static byte[] MakeTiffBytes(int width, int height, byte[] rgbPixels)
    {
        const int ifdEntryCount = 10;
        int ifdOffset = 8;
        int bpsOffset = ifdOffset + 2 + ifdEntryCount * 12 + 4;
        int pixelOffset = bpsOffset + 6;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)'I');
        w.Write((byte)'I');
        w.Write((short)42); // magic
        w.Write(ifdOffset);
        w.Write((short)ifdEntryCount);
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            w.Write(tag);
            w.Write(type);
            w.Write(count);
            w.Write(value);
        }
        // 条目须按 tag 升序
        Entry(256, 3, 1, (uint)width); // ImageWidth
        Entry(257, 3, 1, (uint)height); // ImageLength
        Entry(258, 3, 3, (uint)bpsOffset); // BitsPerSample（3 个 SHORT，外置）
        Entry(259, 3, 1, 1); // Compression = None
        Entry(262, 3, 1, 2); // Photometric = RGB
        Entry(273, 4, 1, (uint)pixelOffset); // StripOffsets
        Entry(277, 3, 1, 3); // SamplesPerPixel
        Entry(278, 4, 1, (uint)height); // RowsPerStrip
        Entry(279, 4, 1, (uint)rgbPixels.Length); // StripByteCounts
        Entry(284, 3, 1, 1); // PlanarConfiguration = Chunky
        w.Write(0u); // 无后续 IFD
        w.Write((short)8);
        w.Write((short)8);
        w.Write((short)8);
        w.Write(rgbPixels);
        return ms.ToArray();
    }
}
