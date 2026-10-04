namespace Inpaint.Core;

/// <summary>
/// 图像原始字节与模型张量布局之间的转换。
/// 位图侧统一使用 Bgra8888 紧凑布局（无 stride padding），
/// 模型侧使用 RGB 平面布局（CHW）。
/// </summary>
public static class ImageProcessing
{
    /// <summary>Bgra8888 → RGB CHW（uint8），供 MI-GAN 修复模型输入。</summary>
    public static byte[] BgraToRgbChw(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var chw = new byte[3 * width * height];
        int plane = width * height;
        for (int i = 0, px = 0; i < plane; i++, px += 4)
        {
            chw[i] = bgra[px + 2];
            chw[plane + i] = bgra[px + 1];
            chw[2 * plane + i] = bgra[px];
        }
        return chw;
    }

    /// <summary>Bgra8888 → RGB CHW（float 0..1），供 Real-ESRGAN 超分模型输入。</summary>
    public static float[] BgraToRgbChwF32(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var chw = new float[3 * width * height];
        int plane = width * height;
        for (int i = 0, px = 0; i < plane; i++, px += 4)
        {
            chw[i] = bgra[px + 2] / 255f;
            chw[plane + i] = bgra[px + 1] / 255f;
            chw[2 * plane + i] = bgra[px] / 255f;
        }
        return chw;
    }

    /// <summary>
    /// 灰度遮罩 → CHW（uint8）。约定与网页版一致：255 = 待修复区域 → 0；其余 → 255。
    /// 遮罩权威数据即每像素 1 字节的灰度（PaintDisc 只写纯白，见 App 层 MaskLayer），
    /// 语义与旧版从 BGRA 位图按 OpenCV 灰度权重提纯白的结果一致。
    /// </summary>
    public static byte[] MaskGrayToChw(ReadOnlySpan<byte> gray)
    {
        var chw = new byte[gray.Length];
        for (int i = 0; i < chw.Length; i++)
            chw[i] = gray[i] == 255 ? (byte)0 : (byte)255;
        return chw;
    }

    /// <summary>RGB CHW（uint8）→ Bgra8888，修复模型输出转回位图。</summary>
    public static byte[] RgbChwToBgra(byte[] chw, int width, int height)
    {
        var bgra = new byte[4 * width * height];
        int plane = width * height;
        for (int i = 0, px = 0; i < plane; i++, px += 4)
        {
            bgra[px + 2] = Clamp255(chw[i]);
            bgra[px + 1] = Clamp255(chw[plane + i]);
            bgra[px] = Clamp255(chw[2 * plane + i]);
            bgra[px + 3] = 255;
        }
        return bgra;
    }

    /// <summary>RGB CHW（float 0..1）→ Bgra8888，超分模型输出转回位图。</summary>
    public static byte[] RgbChwF32ToBgra(float[] chw, int width, int height)
    {
        var bgra = new byte[4 * width * height];
        int plane = width * height;
        for (int i = 0, px = 0; i < plane; i++, px += 4)
        {
            bgra[px + 2] = Clamp01(chw[i]);
            bgra[px + 1] = Clamp01(chw[plane + i]);
            bgra[px] = Clamp01(chw[2 * plane + i]);
            bgra[px + 3] = 255;
        }
        return bgra;
    }

    private static byte Clamp255(int v) => (byte)(v > 255 ? 255 : v < 0 ? 0 : v);

    private static byte Clamp01(float v)
    {
        if (v <= 0f) return 0;
        if (v >= 1f) return 255;
        return (byte)(v * 255f + 0.5f);
    }
}
