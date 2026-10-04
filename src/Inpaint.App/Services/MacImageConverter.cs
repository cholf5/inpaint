using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Inpaint.App.Services;

/// <summary>
/// macOS 剪贴板 TIFF 解码。截图（⌘⇧⌃3/4 等）放进剪贴板的是 public.tiff，
/// Avalonia 只把 public.png 归一为 DataFormat.Bitmap，且 Skia 不支持 TIFF 编解码；
/// 经系统 ImageIO 把 TIFF 解成 BGRA（预乘）像素后直接构造 Bgra8888 位图。
/// 仅 macOS 可用（IsSupported），纯系统框架 P/Invoke，失败返回 null 由调用方兜底提示。
/// </summary>
internal static class MacImageConverter
{
    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>解码 TIFF 字节为位图（取第一页）；数据损坏或非 macOS 返回 null。</summary>
    public static unsafe Bitmap? DecodeTiff(byte[] tiff)
    {
        if (!IsSupported) return null;
        try
        {
            fixed (byte* bytes = tiff)
            {
                var data = CFDataCreate(IntPtr.Zero, (IntPtr)bytes, (nint)tiff.LongLength);
                if (data == IntPtr.Zero) return null;
                try
                {
                    return DecodeFirstPage(data);
                }
                finally
                {
                    CFRelease(data);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? DecodeFirstPage(IntPtr data)
    {
        var source = CGImageSourceCreateWithData(data, IntPtr.Zero);
                if (source == IntPtr.Zero) return null;
        try
        {
            if (CGImageSourceGetCount(source) == 0) return null;
            var image = CGImageSourceCreateImageAtIndex(source, 0, IntPtr.Zero);
            if (image == IntPtr.Zero || CGImageIsMask(image)) return null;
            try
            {
                return RenderToBgra(image);
            }
            finally
            {
                CFRelease(image);
            }
        }
        finally
        {
            CFRelease(source);
        }
    }

    /// <summary>把 CGImage 画进 BGRA（预乘、小端）位图上下文并拷贝为 Avalonia 位图。</summary>
    private static Bitmap? RenderToBgra(IntPtr image)
    {
        int width = (int)CGImageGetWidth(image);
        int height = (int)CGImageGetHeight(image);
        if (width <= 0 || height <= 0) return null;
        var colorspace = CGColorSpaceCreateDeviceRGB();
        if (colorspace == IntPtr.Zero) return null;
        try
        {
            // kCGImageAlphaPremultipliedFirst(2) | kCGBitmapByteOrder32Little(2<<12) → 内存布局 B G R A（预乘）
            const uint premultipliedFirst = 2;
            const uint byteOrder32Little = 2u << 12;
            var context = CGBitmapContextCreate(
                IntPtr.Zero, width, height, 8, width * 4, colorspace,
                byteOrder32Little | premultipliedFirst);
            if (context == IntPtr.Zero) return null;
            try
            {
                CGContextDrawImage(context, new CGRect(0, 0, width, height), image);
                var buffer = CGBitmapContextGetData(context);
                if (buffer == IntPtr.Zero) return null;
                // Bitmap 构造函数会立即拷贝像素，随后的上下文释放不影响结果
                return new Bitmap(
                    PixelFormats.Bgra8888, AlphaFormat.Premul, buffer,
                    new PixelSize(width, height), new Vector(96, 96), width * 4);
            }
            finally
            {
                CFRelease(context);
            }
        }
        finally
        {
            CFRelease(colorspace);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGRect(double x, double y, double width, double height)
    {
        private readonly double X = x, Y = y, Width = width, Height = height;
    }

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDataCreate(IntPtr allocator, IntPtr bytes, nint length);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateWithData(IntPtr data, IntPtr options);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern nint CGImageSourceGetCount(IntPtr source);

    [DllImport("/System/Library/Frameworks/ImageIO.framework/ImageIO")]
    private static extern IntPtr CGImageSourceCreateImageAtIndex(IntPtr source, nint index, IntPtr options);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern nint CGImageGetWidth(IntPtr image);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern nint CGImageGetHeight(IntPtr image);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CGImageIsMask(IntPtr image);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGColorSpaceCreateDeviceRGB();

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGBitmapContextCreate(
        IntPtr data, nint width, nint height, nint bitsPerComponent, nint bytesPerRow,
        IntPtr space, uint bitmapInfo);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGBitmapContextGetData(IntPtr context);
}
