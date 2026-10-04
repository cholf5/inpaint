using Microsoft.ML.OnnxRuntime;

namespace Inpaint.Inference;

internal static class OrtConfig
{
    /// <summary>
    /// allowGpu 表示模型图适合整体上 GPU：Real-ESRGAN 是全卷积图（macOS CoreML：M2 实测
    /// 64×64 tile 540ms(CPU) → 11ms；Windows DirectML：RTX 2070 实测 495ms(CPU) → 23.5ms，
    /// 代价都是会话创建时一次性的模型编译/初始化 3~5 秒），超分引擎传 true；
    /// MI-GAN 有算子不受支持，CoreML 只能接管 559 个节点中的 375 个，11 个分区的来回搬运
    /// 使单次推理从 0.4s 恶化到 69s，因此修复引擎保持 CPU（传 false）。
    ///
    /// EP 策略：macOS 上 Auto/Gpu 都启用 CoreML；Windows 上仅 Gpu 显式选择才启用 DirectML，
    /// Auto 保持 CPU（D3D12 在老显卡/虚拟机/RDP 会话上可用性参差，默认路径不出 GPU 相关错误）；
    /// 其他平台无可用 GPU EP，Gpu 等效 CPU。EP 追加失败（如无 RID 的开发运行没带 DirectML
    /// 原生库）静默回退 CPU；会话创建阶段的设备级失败（无 D3D12 等）由调用方按推理错误上报。
    ///
    /// INPAINT_EP 环境变量优先级最高：cpu 强制全部回退 CPU；coreml/dml 强制超分图启用对应 EP
    /// （仅对 allowGpu=true 生效，MI-GAN 不受影响；MI-GAN 上 CoreML 极慢，仅实验用；
    /// dml 仅 Windows 有意义）。无环境变量时由设置里的 mode 决定。
    ///
    /// 包引用按 RID 条件切换（Inpaint.Inference.csproj）：DirectML EP 只编译进
    /// Microsoft.ML.OnnxRuntime.DirectML 的原生库，该包止于 1.24.4，managed/native 必须同版本
    /// 配对，故 Windows RID 构建整套固定 1.24.4，其余平台维持 1.30.0。
    /// </summary>
    public static SessionOptions MakeSessionOptions(
        bool allowGpu, AccelerationMode mode = AccelerationMode.Auto)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        var ep = Environment.GetEnvironmentVariable("INPAINT_EP");
        bool envForceCpu = ep?.Equals("cpu", StringComparison.OrdinalIgnoreCase) == true;
        bool envForceCoreML = ep?.Equals("coreml", StringComparison.OrdinalIgnoreCase) == true;
        bool envForceDml = ep?.Equals("dml", StringComparison.OrdinalIgnoreCase) == true;
        // macOS 且图适合 GPU 时：环境变量优先，其次设置模式（Cpu 关闭，Gpu/Auto 开启）
        if (OperatingSystem.IsMacOS()
            && allowGpu
            && (envForceCoreML || (!envForceCpu && mode != AccelerationMode.Cpu)))
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
        // Windows 仅显式选 GPU 档（或环境变量强制）时启用 DirectML；Auto/Cpu 保持纯 CPU
        else if (OperatingSystem.IsWindows()
            && allowGpu
            && !envForceCpu
            && (envForceDml || mode == AccelerationMode.Gpu))
        {
            try
            {
                options.AppendExecutionProvider("DML");
            }
            catch
            {
                // 该构建没带 DirectML EP（无 RID 的开发运行用 1.30.0 全家桶原生库）时回退 CPU
            }
        }
        return options;
    }
}
