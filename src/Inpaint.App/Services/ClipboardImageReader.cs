using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;

namespace Inpaint.App.Services;

/// <summary>
/// 剪贴板图片字节兜底读取。Avalonia 已把平台位图格式归一到 DataFormat.Bitmap
/// （Windows 的 PNG/DIB/HBITMAP、macOS/X11 的 public.png，内部即完成解码），
/// 但 macOS 截图放进剪贴板的是 public.tiff（Skia 不支持 TIFF，需经系统 ImageIO），
/// X11/Wayland 的 MIME 字节格式也可能绕过 Bitmap 归一。这里按白名单探测字节格式，
/// 原始字节在 UI 线程取出（平台剪贴板有线程亲和），解码由调用方放后台执行。
/// </summary>
internal static class ClipboardImageReader
{
    /// <summary>Skia 可直接解码的平台字节格式（macOS UTI / Linux MIME）。</summary>
    private static readonly string[] SkiaDecodable =
    [
        "public.jpeg", "com.microsoft.bmp",
        "image/png", "image/jpeg", "image/bmp", "image/webp",
    ];

    /// <summary>按白名单取剪贴板里的图片原始字节；命中 TIFF 单独标记（走 ImageIO 解码）。</summary>
    public static async Task<(byte[] Bytes, bool IsTiff)?> TryGetImageBytesAsync(IAsyncDataTransfer dataTransfer)
    {
        foreach (var format in dataTransfer.Formats)
        {
            if (format is not DataFormat<byte[]> { Kind: DataFormatKind.Platform } bytesFormat) continue;
            bool isTiff = bytesFormat.Identifier is "public.tiff";
            if (!isTiff && Array.IndexOf(SkiaDecodable, bytesFormat.Identifier) < 0) continue;
            if (await dataTransfer.TryGetValueAsync(bytesFormat) is { } bytes)
                return (bytes, isTiff);
        }
        return null;
    }

    /// <summary>把剪贴板字节解码为位图：TIFF 经 macOS ImageIO，其余走 Skia。失败返回 null。</summary>
    public static Bitmap? DecodeImageBytes(byte[] bytes, bool isTiff)
    {
        if (isTiff) return MacImageConverter.DecodeTiff(bytes);
        try
        {
            return new Bitmap(new MemoryStream(bytes, writable: false));
        }
        catch
        {
            return null;
        }
    }
}
