namespace Inpaint.Inference;

/// <summary>
/// 执行设备偏好（由设置界面选择，经引擎构造/创建方法传入）：
/// Auto 沿用平台默认策略，Cpu 强制 CPU，Gpu 强制 GPU 加速（macOS 上为 CoreML，其他平台暂无对应 EP，等效 CPU）。
/// 调试环境变量 INPAINT_EP 优先级高于此设置，见 OrtConfig。
/// </summary>
public enum AccelerationMode
{
    Auto,
    Cpu,
    Gpu,
}
