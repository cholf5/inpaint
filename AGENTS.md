# AGENTS.md

lxfater/inpaint-web 的 C# / .NET 10 + Avalonia 桌面重写：MI-GAN 图片修复 + Real-ESRGAN ×4 高清化，纯本地 ONNX Runtime 推理，无服务器、无 JS。

## 构建 / 运行

- 需要 .NET 10 SDK。`dotnet build Inpaint.slnx`；`dotnet run --project src/Inpaint.App`。
- 单元测试在 `tests/Inpaint.Tests`（xunit.v3 + Avalonia.Headless），`dotnet test` 运行；覆盖 Core 布局/遮罩转换、Inference 分块语义（`UpscaleEngine.FillTile`/`CopyTileCore` 为 internal，经 `InternalsVisibleTo` 供测试）与 App 层（ViewModel 生命周期、画布指针输入，`[AvaloniaFact]` 走 headless）。不含需要模型文件或 ONNX session 的路径。**没有 .editorconfig / 格式化配置**——除测试外，验证手段就是编译通过加手动运行。
- headless 测试入口 `TestAppBuilder` 必须用 `UseHeadlessDrawing = false` + `.UseSkia()`：headless 自绘位图的 `WriteableBitmap.Lock`/`CopyPixels` 语义与生产 Skia 不一致，会得到假结果。注意 App 命名空间与同名命名空间冲突（`Inpaint.App.App` 需别名）。

## 目录与分层

- `inpaint-web/`：原网页版（React/TS/Vite）参考实现，**不在 .NET 解决方案内、未被 git 跟踪**。移植语义（遮罩 markProcess、分块 tileProc、下载 ensureModel）以它为基准；不要修改或提交它。
- 依赖只允许向下：`Inpaint.Core`（零包引用，图像布局转换）← `Inpaint.Inference`（仅 ONNX Runtime）← `Inpaint.App`（Avalonia + CommunityToolkit.Mvvm）。

## 关键模型语义（移植自网页版，改动前先对照 inpaint-web/src/utils.ts）

- **mask 张量：0 = 待修复，255 = 保留**。UI 白色笔触（灰度恰为 255，灰度权重同 OpenCV BGR2GRAY）映射为 0，见 `ImageProcessing.MaskBgraToChw`。
- MI-GAN（`migan_pipeline_v2.onnx`）：输入 image `[1,3,H,W]` uint8（RGB）+ mask `[1,1,H,W]` uint8，前后处理都在模型内完成。
- Real-ESRGAN（`realesrgan-x4.onnx`）：输入 float 0..1 RGB CHW；64×64 tile、四周外扩 6px 重叠、越界钳制到边缘像素，核心区 52×52，输出 ×4。
- 位图侧统一 Bgra8888 紧凑布局，模型侧 RGB CHW（平面式）；转换全部在 Core。

## 推理与模型缓存

- 模型首次使用时由 `ModelStore` 下载（HuggingFace 主源 + CDN 备源，临时文件原子替换），缓存到应用数据目录：macOS `~/Library/Application Support/Inpaint/models/`（Windows `%APPDATA%\Inpaint`、Linux `~/.local/share/Inpaint`）。网络受限时设 `https_proxy` 或手动放入文件。
- 默认 CPU EP（`OrtConfig.MakeSessionOptions`）；`INPAINT_EP=coreml` 环境变量在 macOS 启用 CoreML（每次启动重新编译模型，数十秒一次性开销）。Windows DML 需加 `Microsoft.ML.OnnxRuntime.DirectML` 包并在同一处追加 EP。
- 输入/输出张量名从 session metadata 探测，带兜底默认值（超分 `"input.1"` / `"1895"`）。

## 代码约定

- 全部项目 `net10.0` + ImplicitUsings + Nullable；注释、XML doc、UI 字符串用中文（与现状一致）。
- App 层 MVVM 用 CommunityToolkit.Mvvm 源生成器：`[ObservableProperty]`、`[RelayCommand(CanExecute=...)]` + `NotifyCanExecuteChangedFor`；命令可用性统一由 `IsBusy` gate。
- 耗时工作 `Task.Run` 下放线程池，UI 更新走 `IProgress<T>`；用户可见错误写入 `StatusText`，不向 UI 抛异常。
- 像素级位图访问用 unsafe 指针（App 已开 AllowUnsafeBlocks）。**WriteableBitmap 有 RowBytes stride，逐行拷贝必须用它**，参见 `MainWindowViewModel.CreateBitmap/ExtractBgra`、`ImageEditorControl.PaintDisc`；Core 的转换函数则假设紧凑无 padding。
- Bitmap 生命周期：历史列表里的 Bitmap 由 ViewModel 统一 Dispose（`AdoptBitmap`/`PushHistory`），新增产生位图的路径注意别泄漏；撤销历史上限 25 步。
