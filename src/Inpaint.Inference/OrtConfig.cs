using Microsoft.ML.OnnxRuntime;

namespace Inpaint.Inference;

internal static class OrtConfig
{
    /// <summary>
    /// allowCoreML 表示模型图适合 CoreML：Real-ESRGAN 是全卷积图可整体上 GPU/ANE
    /// （M2 实测 64×64 tile 540ms(CPU) → 11ms，代价是会话创建时编译模型 3~4 秒），超分引擎传 true；
    /// MI-GAN 有算子不受支持，CoreML 只能接管 559 个节点中的 375 个，11 个分区的来回搬运
    /// 使单次推理从 0.4s 恶化到 69s，因此修复引擎保持 CPU（传 false）。
    /// INPAINT_EP=cpu 强制两个引擎都用 CPU；INPAINT_EP=coreml 强制启用 CoreML（MI-GAN 上极慢，仅实验用）。
    /// Windows 若引用 Microsoft.ML.OnnxRuntime.DirectML 可在此追加 DML EP。
    /// </summary>
    public static SessionOptions MakeSessionOptions(bool allowCoreML)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        var ep = Environment.GetEnvironmentVariable("INPAINT_EP");
        bool forceCpu = ep?.Equals("cpu", StringComparison.OrdinalIgnoreCase) == true;
        bool forceCoreML = ep?.Equals("coreml", StringComparison.OrdinalIgnoreCase) == true;
        if (OperatingSystem.IsMacOS() && (forceCoreML || (allowCoreML && !forceCpu)))
        {
            try
            {
                options.AppendExecutionProvider("CoreML");
            }
            catch
            {
                // 该版本/机型无 CoreML EP 时回退 CPU
            }
        }
        return options;
    }
}
