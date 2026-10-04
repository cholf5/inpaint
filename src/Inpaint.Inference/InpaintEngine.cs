using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Inpaint.Inference;

/// <summary>
/// MI-GAN 修复模型（migan_pipeline_v2.onnx）。
/// 输入 image [1,3,H,W] uint8（RGB）+ mask [1,1,H,W] uint8（待修复=0，保留=255），
/// 输出 "result" 与输入同尺寸的 RGB CHW uint8。前后处理均已在模型内完成。
/// </summary>
public sealed class InpaintEngine : IDisposable
{
    private InferenceSession? _session;
    private string _imageInput = "image";
    private string _maskInput = "mask";
    private string _outputName = "result";

    public static async Task<InpaintEngine> CreateAsync(
        IProgress<double>? downloadProgress = null, CancellationToken ct = default)
    {
        await ModelStore.EnsureAsync(KnownModels.MiganPipeline, downloadProgress, ct);
        return new InpaintEngine();
    }

    /// <summary>image/mask 尺寸必须一致；返回值布局与 imageChw 相同。</summary>
    public byte[] Run(int width, int height, byte[] imageChw, byte[] maskChw)
    {
        if (_session is null)
        {
            _session = new InferenceSession(
                ModelStore.GetPath(KnownModels.MiganPipeline), OrtConfig.MakeSessionOptions(allowGpu: false));
            var inputNames = _session.InputMetadata.Keys.ToArray();
            _imageInput = inputNames.ElementAtOrDefault(0) ?? _imageInput;
            _maskInput = inputNames.ElementAtOrDefault(1) ?? _maskInput;
            _outputName = _session.OutputMetadata.Keys.FirstOrDefault() ?? _outputName;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(
                _imageInput, new DenseTensor<byte>(new Memory<byte>(imageChw), new[] { 1, 3, height, width })),
            NamedOnnxValue.CreateFromTensor(
                _maskInput, new DenseTensor<byte>(new Memory<byte>(maskChw), new[] { 1, 1, height, width })),
        };

        using var results = _session.Run(inputs);
        var value = results.First(v => v.Name == _outputName).Value;
        if (value is DenseTensor<byte> tensor) return tensor.Buffer.ToArray();
        if (value is DenseTensor<float> floatTensor)
        {
            var span = floatTensor.Buffer.Span;
            var bytes = new byte[span.Length];
            for (int i = 0; i < span.Length; i++)
            {
                float v = span[i];
                bytes[i] = (byte)(v <= 0 ? 0 : v >= 255 ? 255 : v);
            }
            return bytes;
        }
        throw new InvalidOperationException(
            $"意外的模型输出类型：{value?.GetType().Name ?? "null"}");
    }

    public void Dispose() => _session?.Dispose();
}
