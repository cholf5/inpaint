# Inpaint

[Inpaint-web](./inpaint-web)（lxfater/inpaint-web，基于 WebGPU/WASM 的浏览器端图片修复工具）的 **C# / Avalonia 桌面版重写**。纯本地运行：模型推理、图像处理、UI 全部是 C#，无 JavaScript、无服务器，图片数据不出本机。

功能：

- **图片修复（Inpaint）**：用画笔涂抹想要移除的内容，MI-GAN 根据周围像素自动填补。
- **高清化（Super-Resolution ×4）**：Real-ESRGAN 分块放大，带接缝抑制。
- 打开 / 拖放图片、保存 PNG、撤销历史（最多 25 步）、回到原图。

## 构建

需要 .NET 10 SDK。

```bash
dotnet build Inpaint.slnx
dotnet run --project src/Inpaint.App
```

## 结构

| 项目 | 职责 |
|---|---|
| `src/Inpaint.Core` | 图像布局转换（Bgra8888 ↔ RGB CHW、mask 二值化），零依赖 |
| `src/Inpaint.Inference` | ONNX Runtime 推理：模型下载与本地缓存、MI-GAN 修复、Real-ESRGAN 分块超分 |
| `src/Inpaint.App` | Avalonia UI：画笔编辑控件、历史管理、进度与状态 |

## 模型

与网页版使用相同的 ONNX 模型（`migan_pipeline_v2.onnx`、`realesrgan-x4.onnx`）。首次使用对应功能时自动从 HuggingFace 下载（失败时切换备用源），缓存到应用数据目录：

- macOS: `~/Library/Application Support/Inpaint/models/`
- Windows: `%APPDATA%\Inpaint\models\`
- Linux: `~/.local/share/Inpaint/models/`

网络受限时可手动下载模型放入上述目录。HuggingFace 直连不通时可用环境变量代理（`https_proxy` 等），或从 hf-mirror.com 下载后手动放置。

## 关键约定（移植自网页版）

- 模型输入：`image [1,3,H,W] uint8`（RGB）+ `mask [1,1,H,W] uint8`；**mask 中 0 = 待修复区域，255 = 保留**（与网页版 markProcess 语义一致，已经实验验证）。
- 超分分块：64×64 tile，四周外扩 6px 重叠、越界钳制到边缘像素，核心区 52×52，输出 4 倍。

## GPU 加速

默认使用 CPU EP（快且可预期）。可选优化：

- macOS：设置环境变量 `INPAINT_EP=coreml` 启用 CoreML EP（注意：每次启动会话需重新编译模型，有数十秒一次性开销）。
- Windows：引用 `Microsoft.ML.OnnxRuntime.DirectML` 并在 `OrtConfig.MakeSessionOptions` 中追加 DML EP。
