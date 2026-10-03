using Inpaint.Core;

namespace Inpaint.Tests;

/// <summary>
/// 钉住与网页版一致的布局与遮罩语义（见 AGENTS.md「关键模型语义」）：
/// 位图侧 Bgra8888 紧凑布局，模型侧 RGB CHW；遮罩 0 = 待修复，255 = 保留。
/// </summary>
public class ImageProcessingTests
{
    // 构造一段 BGRA 像素：第 i 个像素 B=i%256, G=(i*7)%256, R=(i*13)%256, A=200-i%200。
    private static byte[] MakeBgra(int width, int height)
    {
        var bgra = new byte[4 * width * height];
        for (int i = 0; i < width * height; i++)
        {
            bgra[i * 4 + 0] = (byte)(i % 256);
            bgra[i * 4 + 1] = (byte)(i * 7 % 256);
            bgra[i * 4 + 2] = (byte)(i * 13 % 256);
            bgra[i * 4 + 3] = (byte)(200 - i % 200);
        }
        return bgra;
    }

    [Fact]
    public void BgraToRgbChw_平面按RGB排列_行主序()
    {
        // 2×2，四个像素颜色互不相同，便于核对每个通道平面的落位。
        var bgra = new byte[]
        {
            1, 2, 3, 255,   // 像素(0,0) B=1 G=2 R=3
            4, 5, 6, 255,   // 像素(1,0)
            7, 8, 9, 255,   // 像素(0,1)
            10, 11, 12, 255, // 像素(1,1)
        };

        var chw = ImageProcessing.BgraToRgbChw(bgra, 2, 2);

        Assert.Equal(12, chw.Length);
        // R 平面：0..3 依行主序取 R 分量
        Assert.Equal(new byte[] { 3, 6, 9, 12 }, chw[..4]);
        // G 平面
        Assert.Equal(new byte[] { 2, 5, 8, 11 }, chw[4..8]);
        // B 平面
        Assert.Equal(new byte[] { 1, 4, 7, 10 }, chw[8..12]);
    }

    [Fact]
    public void BgraToRgbChw_忽略Alpha()
    {
        var bgra = MakeBgra(3, 2);
        var chw = ImageProcessing.BgraToRgbChw(bgra, 3, 2);
        // 输出只含 RGB，无 alpha 通道
        Assert.Equal(3 * 3 * 2, chw.Length);
    }

    [Fact]
    public void BgraToRgbChwF32_归一化到0到1()
    {
        var bgra = new byte[] { 0, 128, 255, 255 };

        var chw = ImageProcessing.BgraToRgbChwF32(bgra, 1, 1);

        Assert.Equal(1f, chw[0]);              // R=255
        Assert.Equal(128f / 255f, chw[1]);     // G=128
        Assert.Equal(0f, chw[2]);              // B=0
    }

    [Fact]
    public void BgraToRgbChwF32_与uint8版本布局一致()
    {
        var bgra = MakeBgra(5, 4);
        var u8 = ImageProcessing.BgraToRgbChw(bgra, 5, 4);
        var f32 = ImageProcessing.BgraToRgbChwF32(bgra, 5, 4);

        Assert.Equal(u8.Length, f32.Length);
        for (int i = 0; i < u8.Length; i++)
            Assert.Equal(u8[i] / 255f, f32[i]);
    }

    [Fact]
    public void MaskBgraToChw_纯白笔触为0_其余为255()
    {
        // 与 PaintDisc 的实际像素一致：不透明白色笔触 + 透明黑背景。
        var bgra = new byte[]
        {
            0, 0, 0, 0,       // 透明背景 → 255（保留）
            255, 255, 255, 255, // 不透明白 → 0（待修复）
            0, 0, 0, 255,     // 不透明黑 → 255
        };

        var mask = ImageProcessing.MaskBgraToChw(bgra, 3, 1);

        Assert.Equal(new byte[] { 255, 0, 255 }, mask);
    }

    [Fact]
    public void MaskBgraToChw_灰度权重与OpenCV一致_仅纯白命中255()
    {
        // 灰度 = (R*77 + G*150 + B*29) >> 8。浅红 (B=0,G=0,R=255) 灰度 76，不算笔触。
        Assert.Equal(255, ImageProcessing.MaskBgraToChw(new byte[] { 0, 0, 255, 255 }, 1, 1)[0]);
        // 254 的灰：254*256>>8 = 254，仍不算笔触。
        Assert.Equal(255, ImageProcessing.MaskBgraToChw(new byte[] { 254, 254, 254, 255 }, 1, 1)[0]);
        // 纯白恰好 255（255*256>>8），命中待修复。
        Assert.Equal(0, ImageProcessing.MaskBgraToChw(new byte[] { 255, 255, 255, 255 }, 1, 1)[0]);
    }

    [Fact]
    public void MaskBgraToChw_忽略Alpha_与当前UI行为一致()
    {
        // PaintDisc 写入的是不透明白；透明白现实中不会出现，这里钉住 alpha 不参与灰度的现状。
        Assert.Equal(0, ImageProcessing.MaskBgraToChw(new byte[] { 255, 255, 255, 0 }, 1, 1)[0]);
    }

    [Fact]
    public void MaskBgraToChw_输出长度为像素数_行主序()
    {
        var bgra = MakeBgra(4, 3);
        bgra[8] = 255; bgra[9] = 255; bgra[10] = 255; // 像素(2,0) 改为白
        var mask = ImageProcessing.MaskBgraToChw(bgra, 4, 3);

        Assert.Equal(12, mask.Length);
        Assert.Equal(0, mask[2]);
        Assert.Equal(11, mask.Count(v => v == 255)); // 其余全部保留
    }

    [Fact]
    public void RgbChwToBgra_与BgraToRgbChw互逆()
    {
        var bgra = MakeBgra(7, 5);
        var chw = ImageProcessing.BgraToRgbChw(bgra, 7, 5);

        var restored = ImageProcessing.RgbChwToBgra(chw, 7, 5);

        Assert.Equal(4 * 7 * 5, restored.Length);
        for (int i = 0; i < 7 * 5; i++)
        {
            Assert.Equal(bgra[i * 4 + 0], restored[i * 4 + 0]); // B
            Assert.Equal(bgra[i * 4 + 1], restored[i * 4 + 1]); // G
            Assert.Equal(bgra[i * 4 + 2], restored[i * 4 + 2]); // R
            Assert.Equal(255, restored[i * 4 + 3]);             // alpha 恒为 255
        }
    }

    [Fact]
    public void RgbChwF32ToBgra_范围钳制与四舍五入()
    {
        // R 平面：-0.5, 0, 0.5, 1.5；其余平面 0。
        var chw = new float[] { -0.5f, 0f, 0.5f, 1.5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };

        var bgra = ImageProcessing.RgbChwF32ToBgra(chw, 4, 1);

        Assert.Equal(0, bgra[2]);      // R=-0.5 → 0
        Assert.Equal(0, bgra[6]);      // R=0
        Assert.Equal(128, bgra[10]);   // R=0.5 → 0.5*255+0.5 = 128
        Assert.Equal(255, bgra[14]);   // R=1.5 → 255
        Assert.All(bgra.Where((_, i) => i % 4 == 3), a => Assert.Equal(255, a));
    }

    [Fact]
    public void RgbChwF32ToBgra_与BgraToRgbChwF32互逆()
    {
        var bgra = MakeBgra(6, 6);
        var f32 = ImageProcessing.BgraToRgbChwF32(bgra, 6, 6);

        var restored = ImageProcessing.RgbChwF32ToBgra(f32, 6, 6);

        for (int i = 0; i < 6 * 6; i++)
        {
            Assert.Equal(bgra[i * 4 + 0], restored[i * 4 + 0]);
            Assert.Equal(bgra[i * 4 + 1], restored[i * 4 + 1]);
            Assert.Equal(bgra[i * 4 + 2], restored[i * 4 + 2]);
        }
    }
}
