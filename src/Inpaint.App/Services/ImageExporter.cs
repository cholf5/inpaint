using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Inpaint.App.Services;

/// <summary>导出格式。JPEG/WebP 为有损编码（带质量参数），PNG 无损。</summary>
public enum ExportFormat
{
    Png,
    Jpeg,
    WebP,
}

/// <summary>一次导出的格式与质量参数（对话框初始值的记忆单位）。</summary>
public sealed record ExportOptions(ExportFormat Format, int Quality);

/// <summary>导出对话框的最终产物：参数 + 按该参数实际编码好的字节（预估即编码，保存直接落盘不再重复编码）。</summary>
public sealed record ExportChoice(ExportFormat Format, int Quality, byte[] Bytes);

/// <summary>
/// 导出编码：把历史树里的位图编码为 PNG / JPEG / WebP 字节，编码器用 Avalonia 自带的 Skia
/// （libpng / libjpeg-turbo / libwebp），不引入新的原生依赖。
/// 语义约定（探针实测）：Avalonia 解码的位图缓冲是预乘 alpha，历史树里新生成的位图全不透明，
/// 两者统一按预乘声明给 Skia；JPEG 不支持 alpha，编码前把非不透明像素合成到白底。
/// </summary>
public static class ImageExporter
{
    /// <summary>JPEG/WebP 的默认质量（导出对话框的初始值）。internal 供单测。</summary>
    internal const int DefaultQuality = 85;

    /// <summary>把位图编码为指定格式字节。quality 仅对 JPEG/WebP 生效（1..100），PNG 无损忽略。</summary>
    public static byte[] Encode(Bitmap bitmap, ExportFormat format, int quality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        quality = Math.Clamp(quality, 1, 100);
        var bgra = ExtractBgra(bitmap);
        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var encodedFormat = format switch
        {
            ExportFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            ExportFormat.WebP => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png,
        };
        if (format == ExportFormat.Jpeg)
            bgra = FlattenOntoWhite(bgra);

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var image = SKImage.FromPixels(info, handle.AddrOfPinnedObject(), width * 4);
            using var data = image.Encode(encodedFormat, quality);
            if (data is null || data.Size == 0)
                throw new InvalidOperationException($"Skia 编码失败：{format}（{width}×{height}）");
            return data.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// JPEG 不支持 alpha：把预乘像素合成到白底（c_premul + 255 - a），全不透明时原样返回零拷贝。
    /// </summary>
    private static byte[] FlattenOntoWhite(byte[] bgra)
    {
        bool hasAlpha = false;
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] == 255) continue;
            hasAlpha = true;
            break;
        }
        if (!hasAlpha) return bgra;
        var result = (byte[])bgra.Clone();
        for (int i = 0; i < result.Length; i += 4)
        {
            int a = result[i + 3];
            if (a == 255) continue;
            result[i] = (byte)(result[i] + (255 - a));
            result[i + 1] = (byte)(result[i + 1] + (255 - a));
            result[i + 2] = (byte)(result[i + 2] + (255 - a));
            result[i + 3] = 255;
        }
        return result;
    }

    /// <summary>读出紧凑 BGRA 字节；Rgba8888 源交换红蓝。internal 供单测。</summary>
    internal static byte[] ExtractBgra(Bitmap bitmap)
    {
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        int stride = 4 * width;
        var result = new byte[4L * width * height];
        bool swap = bitmap.Format is { } format && format == PixelFormats.Rgba8888;
        unsafe
        {
            fixed (byte* dst = result)
            {
                // 直接拷进托管数组：省去 AllocHGlobal 中转的一份全尺寸缓冲与一次整图拷贝
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), (nint)dst, result.Length, stride);
            }
            if (swap)
            {
                fixed (byte* p = result)
                {
                    for (int i = 0; i < result.Length; i += 4)
                        (p[i], p[i + 2]) = (p[i + 2], p[i]);
                }
            }
        }
        return result;
    }

    /// <summary>字节数的人读格式（B / KB / MB），固定小数点避免区域设置差异。internal 供单测。</summary>
    internal static string FormatBytes(long bytes)
    {
        const double KB = 1024, MB = 1024 * 1024;
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < (long)MB => string.Create(CultureInfo.InvariantCulture, $"{bytes / KB:F1} KB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / MB:F1} MB"),
        };
    }
}
