<div align="center">

<img src="src/Inpaint.App/Assets/app-icon.png" width="128" alt="Inpaint 图标"/>

# Inpaint

**纯本地的 AI 图片修复与高清化桌面应用**

[Inpaint-web](https://github.com/lxfater/inpaint-web)（WebGPU/WASM 浏览器版）的 C# / Avalonia 桌面重写：模型推理、图像处理、UI 全部是 C#，无 JavaScript、无服务器，**图片数据不出本机**。

![平台](https://img.shields.io/badge/platform-macOS%20%7C%20Windows%20%7C%20Linux-blue)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![许可证](https://img.shields.io/badge/license-MIT-green)

</div>

## 🎬 演示

<div align="center">

<figure>
  <img src="docs/inpaint.gif" alt="图片修复演示">
  <figcaption><strong>图片修复</strong> —— 用画笔涂抹想要移除的内容，MI-GAN 根据周围像素自动填补</figcaption>
</figure>

</div>

<br>

<div align="center">

<figure>
  <img src="docs/upscale.gif" alt="高清化演示">
  <figcaption><strong>高清化 ×4</strong> —— Real-ESRGAN 分块放大，带接缝抑制</figcaption>
</figure>

</div>

<br>

<div align="center">

<figure>
  <img src="docs/settings.gif" alt="设置演示">
  <figcaption><strong>设置</strong> —— 主题、语言、加速设备等，任何修改即时生效</figcaption>
</figure>

</div>

## ✨ 功能

- **图片修复（Inpaint）**：涂抹即修复（松手立即执行，可在设置中改回按钮式）。
- **高清化（Super-Resolution ×4）**：Real-ESRGAN 分块放大，macOS 上默认 CoreML GPU 加速。
- **精细编辑**：画布缩放 / 平移便于涂抹细节，鼠标滚轮在画布上直接调画笔大小。
- **生成历史**：git 式分叉历史树，撤销 / 分叉 / 回到原图，节点上限可配置（默认 25）。
- **个性化**：深色 / 浅色 / 跟随系统主题，简体中文 / English 界面，默认画笔大小、历史上限等均可在设置中调整。
- **纯本地**：模型首次使用时自动下载并缓存到本机，之后完全离线；启动联网检查更新默认关闭。

## 📦 下载

前往 [Releases](https://github.com/cholf5/inpaint/releases) 下载对应平台的安装包（macOS / Windows 便携版与 Setup 安装包 / Linux）。首次使用修复或高清化功能时会自动下载对应 ONNX 模型。

## 🛠️ 从源码构建

需要 .NET 10 SDK。

```bash
dotnet build Inpaint.slnx
dotnet run --project src/Inpaint.App
```

单元测试：`dotnet test`（无需模型文件，不需要网络）。

## 🧱 项目结构

| 项目 | 职责 |
|---|---|
| `src/Inpaint.Core` | 图像布局转换（Bgra8888 ↔ RGB CHW、mask 二值化），零依赖 |
| `src/Inpaint.Inference` | ONNX Runtime 推理：模型下载与本地缓存、MI-GAN 修复、Real-ESRGAN 分块超分 |
| `src/Inpaint.App` | Avalonia UI：画笔编辑控件、历史管理、进度与状态 |

## 📥 模型

与网页版使用相同的 ONNX 模型（`migan_pipeline_v2.onnx`、`realesrgan-x4.onnx`）。首次使用对应功能时自动从 HuggingFace 下载（失败时切换备用源），缓存到应用数据目录：

- macOS: `~/Library/Application Support/Inpaint/models/`
- Windows: `%APPDATA%\Inpaint\models\`
- Linux: `~/.local/share/Inpaint/models/`

网络受限时可手动下载模型放入上述目录。HuggingFace 直连不通时可用环境变量代理（`https_proxy` 等），或从 hf-mirror.com 下载后手动放置；设置里也可直接打开模型缓存文件夹。

## 🔑 关键约定（移植自网页版）

- 模型输入：`image [1,3,H,W] uint8`（RGB）+ `mask [1,1,H,W] uint8`；**mask 中 0 = 待修复区域，255 = 保留**（UI 白色笔触经灰度权重映射为 0，与网页版 markProcess 语义一致）。
- 超分分块：64×64 tile，四周外扩 6px 重叠、越界钳制到边缘像素，核心区 52×52，输出 4 倍。

## ⚡ GPU 加速

按引擎区分，设置界面可选超分加速设备（自动 / CPU / GPU，对下次会话生效）：

- **超分（Real-ESRGAN，全卷积）**：macOS 默认走 CoreML——M2 实测 64×64 tile 从 540ms(CPU) 降到 11ms，代价是会话编译 3~4 秒的一次性开销。
- **修复（MI-GAN）**：保持 CPU——CoreML 只能接管其 559 个节点中的 375 个，分区搬运反而使单次推理从 0.4s 恶化到 69s。
- 环境变量 `INPAINT_EP` 优先级最高：`cpu` 强制全部回退 CPU，`coreml` 强制启用（MI-GAN 上极慢，仅实验用）。
- Windows 想启用 DirectML：引用 `Microsoft.ML.OnnxRuntime.DirectML` 包并在 `OrtConfig.MakeSessionOptions` 中追加 DML EP。

## 📄 许可证

[MIT](./LICENSE)
