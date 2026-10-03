using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Inpaint.Inference;

/// <summary>
/// Real-ESRGAN ×4 超分模型（realesrgan-x4.onnx，输入名 "input.1"）。
/// 模型按 64×64 固定块推理：核心区 52×52，四周外扩 6px 重叠避免接缝，
/// 越界处钳制到边缘像素；输出为 4 倍尺寸的 RGB CHW float（0..1）。
/// 分块方式与网页版 tileProc 完全一致。
/// </summary>
public sealed class UpscaleEngine : IDisposable
{
    private const int TileSize = 64;
    private const int TilePadding = 6;
    private const int Scale = 4;

    private InferenceSession? _session;
    private string _inputName = "input.1";
    private string _outputName = "1895";

    public static async Task<UpscaleEngine> CreateAsync(
        IProgress<double>? downloadProgress = null, CancellationToken ct = default)
    {
        await ModelStore.EnsureAsync(KnownModels.RealEsrganX4, downloadProgress, ct);
        return new UpscaleEngine();
    }

    /// <summary>imageChw 为 [3,H,W] float 0..1。tileProgress 上报 0..100 分块进度。</summary>
    public float[] Run(
        int width, int height, float[] imageChw,
        IProgress<double>? tileProgress = null, CancellationToken ct = default)
    {
        if (_session is null)
        {
            _session = new InferenceSession(
                ModelStore.GetPath(KnownModels.RealEsrganX4), OrtConfig.MakeSessionOptions(allowCoreML: true));
            _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? _inputName;
            _outputName = _session.OutputMetadata.Keys.FirstOrDefault() ?? _outputName;
        }

        int core = TileSize - TilePadding * 2;
        int tilesX = (width + core - 1) / core;
        int tilesY = (height + core - 1) / core;
        int outW = width * Scale;
        int outH = height * Scale;
        var output = new float[3 * outW * outH];

        var tile = new float[3 * TileSize * TileSize];
        int tilePlane = TileSize * TileSize;

        for (int ty = 0; ty < tilesY; ty++)
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                ct.ThrowIfCancellationRequested();
                int coreW = Math.Min(core, width - tx * core);
                int coreH = Math.Min(core, height - ty * core);

                FillTile(imageChw, width, height, tx * core, ty * core, coreW, coreH, tile, tilePlane);

                var tileTensor = new DenseTensor<float>(
                    new Memory<float>(tile), new[] { 1, 3, TileSize, TileSize });
                using var results = _session.Run(
                    new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor(_inputName, tileTensor),
                    });

                if (results.First(v => v.Name == _outputName).Value is not DenseTensor<float> outTile)
                    throw new InvalidOperationException("超分模型输出类型不是 float32");

                CopyTileCore(outTile, output, outW, outH, tx * core * Scale, ty * core * Scale, coreW * Scale, coreH * Scale);
                tileProgress?.Report((double)(ty * tilesX + tx + 1) / (tilesX * tilesY) * 100);
            }
        }
        return output;
    }

    /// <summary>把源图中 (offX, offY) 起的核心区外扩 padding 组装成 64×64 tile，越界钳制到边缘像素。internal 供分块单测。</summary>
    internal static void FillTile(
        float[] src, int srcW, int srcH, int offX, int offY, int coreW, int coreH,
        float[] tile, int tilePlane)
    {
        Array.Clear(tile);
        for (int yp = -TilePadding; yp < coreH + TilePadding; yp++)
        {
            int yim = Math.Clamp(offY + yp, 0, srcH - 1);
            int yt = yp + TilePadding;
            for (int xp = -TilePadding; xp < coreW + TilePadding; xp++)
            {
                int xim = Math.Clamp(offX + xp, 0, srcW - 1);
                int xt = xp + TilePadding;
                int srcIndex = yim * srcW + xim;
                int tileIndex = yt * TileSize + xt;
                tile[tileIndex] = src[srcIndex];
                tile[tilePlane + tileIndex] = src[srcW * srcH + srcIndex];
                tile[2 * tilePlane + tileIndex] = src[2 * srcW * srcH + srcIndex];
            }
        }
    }

    /// <summary>从 256×256 的 tile 输出中拷出核心区（去掉 padding 的 4 倍放大结果）。internal 供分块单测。</summary>
    internal static void CopyTileCore(
        DenseTensor<float> outTile, float[] output, int outW, int outH,
        int dstX, int dstY, int coreW, int coreH)
    {
        int outTileSize = TileSize * Scale;
        int outTilePlane = outTileSize * outTileSize;
        var data = outTile.Buffer.Span;
        int pad = TilePadding * Scale;

        for (int y = 0; y < coreH; y++)
        {
            int srcRow = (y + pad) * outTileSize + pad;
            int dstRow = (dstY + y) * outW + dstX;
            for (int x = 0; x < coreW; x++)
            {
                int dstIndex = dstRow + x;
                int srcIndex = srcRow + x;
                output[dstIndex] = data[srcIndex];
                output[outW * outH + dstIndex] = data[outTilePlane + srcIndex];
                output[2 * outW * outH + dstIndex] = data[2 * outTilePlane + srcIndex];
            }
        }
    }

    public void Dispose() => _session?.Dispose();
}
