using Inpaint.Inference;

namespace Inpaint.Tests;

/// <summary>
/// 钉住 UpscaleEngine 与网页版 tileProc 一致的分块语义：
/// 64×64 tile、四周外扩 6px、越界钳制到边缘像素、核心区 52×52、输出拷贝时剥离 padding。
/// </summary>
public class UpscaleTileTests
{
    private const int Tile = 64;
    private const int Plane = Tile * Tile;
    private const int Padding = 6;

    /// <summary>源图 R/G/B 平面分别编码为 p*100000 + y*100 + x，便于精确核对每个落位。</summary>
    private static float[] MakeSource(int width, int height)
    {
        var src = new float[3 * width * height];
        for (int p = 0; p < 3; p++)
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    src[p * width * height + y * width + x] = p * 100000f + y * 100f + x;
        return src;
    }

    private static float ExpectedSource(int p, int y, int x) =>
        p * 100000f + y * 100f + x;

    [Fact]
    public void FillTile_内部tile_整个64x64被核心区外扩填满()
    {
        const int srcW = 100, srcH = 80;
        var src = MakeSource(srcW, srcH);
        var tile = new float[3 * Plane];

        // off (20,10)：外扩后 14..77 × 4..67 均在源图内，不触发钳制。
        UpscaleEngine.FillTile(src, srcW, srcH, offX: 20, offY: 10, coreW: 52, coreH: 52, tile, Plane);

        for (int yt = 0; yt < Tile; yt++)
        {
            for (int xt = 0; xt < Tile; xt++)
            {
                int xim = 20 + xt - Padding, yim = 10 + yt - Padding;
                Assert.Equal(ExpectedSource(0, yim, xim), tile[yt * Tile + xt]);
                Assert.Equal(ExpectedSource(1, yim, xim), tile[Plane + yt * Tile + xt]);
                Assert.Equal(ExpectedSource(2, yim, xim), tile[2 * Plane + yt * Tile + xt]);
            }
        }
    }

    [Fact]
    public void FillTile_越界钳制到边缘像素_未填满区域保持0()
    {
        const int srcW = 100, srcH = 80;
        var src = MakeSource(srcW, srcH);
        var tile = new float[3 * Plane];

        // 100×80 源图的右下角 tile：off (52,52)，核心区 48×28，右侧/下侧越界。
        UpscaleEngine.FillTile(src, srcW, srcH, offX: 52, offY: 52, coreW: 48, coreH: 28, tile, Plane);

        // 填充区 = 核心区 + 四周 6px 外扩：(48+12)×(28+12) = 60×40。
        for (int yt = 0; yt < 40; yt++)
        {
            for (int xt = 0; xt < 60; xt++)
            {
                int xim = Math.Clamp(52 + xt - Padding, 0, srcW - 1);
                int yim = Math.Clamp(52 + yt - Padding, 0, srcH - 1);
                Assert.Equal(ExpectedSource(0, yim, xim), tile[yt * Tile + xt]);
            }
        }
        // 右下角与底边右端都钳制到源图最后一个像素。
        Assert.Equal(ExpectedSource(0, srcH - 1, srcW - 1), tile[39 * Tile + 59]);
        // 填充区之外保持清零。
        Assert.Equal(0f, tile[0 * Plane + 40 * Tile + 0]);
        Assert.Equal(0f, tile[2 * Plane + 0 * Tile + 60]);
    }

    [Fact]
    public void CopyTileCore_剥离padding并按4倍尺寸落位()
    {
        // tile 输出按线性索引编码，核对拷贝时 padding(6px×4=24) 被剥离。
        var outTileData = new float[3 * 256 * 256];
        for (int p = 0; p < 3; p++)
            for (int i = 0; i < 256 * 256; i++)
                outTileData[p * 256 * 256 + i] = p * 65536f + i;
        var outTile = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(
            new Memory<float>(outTileData), new[] { 1, 3, 256, 256 });

        // 宽 53 的源图：outW=212，tile0 核心 208 宽，右侧 4 列本 tile 不负责。
        const int outW = 212, outH = 4;
        var output = new float[3 * outW * outH];

        UpscaleEngine.CopyTileCore(outTile, output, outW, outH, dstX: 0, dstY: 0, coreW: 208, coreH: 4);

        for (int p = 0; p < 3; p++)
        {
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 208; x++)
                {
                    float expected = p * 65536f + (y + 24) * 256 + (x + 24);
                    Assert.Equal(expected, output[p * outW * outH + y * outW + x]);
                }
                // 核心区之外保持 0（其他 tile 的责任区）
                for (int x = 208; x < outW; x++)
                    Assert.Equal(0f, output[p * outW * outH + y * outW + x]);
            }
        }
    }

    [Fact]
    public void CopyTileCore_相邻tile不重叠()
    {
        var outTileData = new float[3 * 256 * 256];
        for (int p = 0; p < 3; p++)
            for (int i = 0; i < 256 * 256; i++)
                outTileData[p * 256 * 256 + i] = p * 65536f + i;
        var outTile = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(
            new Memory<float>(outTileData), new[] { 1, 3, 256, 256 });

        const int outW = 212, outH = 4;
        var output = new float[3 * outW * outH];

        UpscaleEngine.CopyTileCore(outTile, output, outW, outH, dstX: 0, dstY: 0, coreW: 208, coreH: 4);
        // 第二个 tile 负责剩余 4 列，同一 outTile 只是数据源。
        UpscaleEngine.CopyTileCore(outTile, output, outW, outH, dstX: 208, dstY: 0, coreW: 4, coreH: 4);

        for (int p = 0; p < 3; p++)
        {
            for (int y = 0; y < outH; y++)
            {
                for (int x = 0; x < outW; x++)
                {
                    int dst = p * outW * outH + y * outW + x;
                    // 每列都取自身 tile 的核心区起点 +24 的偏移，两段各自连续且不重叠。
                    int tileX = x < 208 ? x : x - 208;
                    Assert.Equal(p * 65536f + (y + 24) * 256 + (tileX + 24), output[dst]);
                }
            }
        }
    }
}
