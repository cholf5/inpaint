using Microsoft.ML.OnnxRuntime;

namespace Inpaint.Inference;

internal static class OrtConfig
{
    /// <summary>
    /// 默认 CPU EP：推理快且可预期（CoreML EP 每次创建会话都要重新编译模型，开销数十秒，
    /// 得不偿失）。设置环境变量 INPAINT_EP=coreml 可显式启用 CoreML。
    /// Windows 若引用 Microsoft.ML.OnnxRuntime.DirectML 可在此追加 DML EP。
    /// </summary>
    public static SessionOptions MakeSessionOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        if (Environment.GetEnvironmentVariable("INPAINT_EP")?.Equals(
                "coreml", StringComparison.OrdinalIgnoreCase) == true && OperatingSystem.IsMacOS())
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
