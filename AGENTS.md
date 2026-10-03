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
- EP 选择在 `OrtConfig.MakeSessionOptions(allowCoreML, mode)`，按引擎区分：超分（全卷积）macOS 默认 CoreML——M2 实测 64×64 tile 540ms(CPU)→11ms，会话编译 3~4 秒一次性开销；修复（MI-GAN）保持 CPU——CoreML 只能接管其 559 个节点中的 375 个，分区搬运使单次推理 0.4s 恶化到 69s。超分设备可由设置界面选择（`AccelerationMode`，经 `UpscaleEngine` 传入，会话懒创建故只对下次会话生效，设备变更时 ViewModel 丢弃已建会话、繁忙则推迟到空闲）；`INPAINT_EP` 环境变量优先级最高：`cpu` 强制全部回退 CPU，`coreml` 强制启用（MI-GAN 上极慢，仅实验用）。Windows DML 需加 `Microsoft.ML.OnnxRuntime.DirectML` 包并在同一处追加 EP。
- 输入/输出张量名从 session metadata 探测，带兜底默认值（超分 `"input.1"` / `"1895"`）。

## 代码约定

- 全部项目 `net10.0` + ImplicitUsings + Nullable；注释、XML doc 用中文。**用户可见 UI 字符串一律经 `Translations.Instance`**（zh 源词典 + en 覆盖，键 = 属性名用 `nameof` 对齐；新增字符串两份词典都要补，XAML 绑定 `{Binding X, Source={x:Static loc:Translations.Instance}}`），默认简体中文。

## 设置与本地化

- 设置运行期是共享单实例 `AppSettings`（App 启动 `SettingsService.Load()` 后传给 MainWindow/ViewModel/设置窗口）：主题、语言、Real-ESRGAN 设备（`AccelerationMode`）、默认画笔大小、生成历史上限。任何属性变更即时生效并落盘——主题/语言由 `App` 订阅应用，设备由主 ViewModel 订阅重建超分引擎。SettingsWindow 只是编辑视图（`SettingsViewModel` 用跨语言稳定的 `OptionItem` 实例数组 + SelectedIndex 映射枚举，切语言只改 Label、不重建 ItemsSource，XAML 须配 ItemTemplate 绑定 Label；重建式换文案会异步清空 ComboBox 选区并把旧选中项经双向绑定推回），不拥有持久化。
- `SettingsService`：settings.json 与模型缓存同目录（`…/Inpaint/settings.json`），枚举存名字、临时文件原子替换；文件缺失/损坏回退默认值，数值越界收敛到合法区间（手改文件兜底）。`Load/Save` 的 path 参数供单测注入临时路径。
- 语言切换 = `Translations.SetLanguage` 逐属性 raise PropertyChanged（静态字段按声明顺序初始化，**词典必须先于 `Instance`**）；StatusText 等瞬态文本与历史节点标题不回溯刷新（无瞬态状态时的初始提示经 VM 派生属性 StatusDisplay 随语言刷新）。测试断言中文字符串的类须在构造函数固定 `SetLanguage(SimplifiedChinese)`，且程序集已禁用集合并行（Translations 是进程级单例）；切语言会牵动 headless App 启动时创建的 MainWindow 绑定，相关测试须走 `[AvaloniaFact]`（UI 线程），普通 `[Fact]` 里切语言会跨线程崩溃。
- App 层 MVVM 用 CommunityToolkit.Mvvm 源生成器：`[ObservableProperty]`、`[RelayCommand(CanExecute=...)]` + `NotifyCanExecuteChangedFor`；命令可用性统一由 `IsBusy` gate。
- 耗时工作 `Task.Run` 下放线程池，UI 更新走 `IProgress<T>`；用户可见错误写入 `StatusText`，不向 UI 抛异常。
- 像素级位图访问用 unsafe 指针（App 已开 AllowUnsafeBlocks）。**WriteableBitmap 有 RowBytes stride，逐行拷贝必须用它**，参见 `MainWindowViewModel.CreateBitmap/ExtractBgra`、`ImageEditorControl.PaintDisc`；Core 的转换函数则假设紧凑无 padding。
- Bitmap 生命周期：生成历史是 git 式分叉树（`ImageHistoryNode`，撤销后生成即分叉；撤销/回到原图=在树上移动当前节点），节点里的 Bitmap/Thumbnail 由 ViewModel 统一 Dispose（`AdoptBitmap`/`PushHistory`/`PruneHistory`），新增产生位图的路径注意别泄漏；节点上限默认 25（`AppSettings.MaxHistory`，设置界面可调），超限优先丢弃最旧的非当前分支，原图（根）永不丢弃。
- 历史面板是整图自绘的竖向 git Graph（`HistoryGraphView`，无列表）：车道为纵向列（0=原图主干），行按创建时间从上往下；**第一子节点延续父车道，基于中间节点生成的新子节点开右侧新车道（原车道不动）**，车道序按分叉发生顺序分配。布局字段（LaneIndex/RowIndex）由 `RebuildHistory` 统一重算，控件只负责绘制/命中/右键菜单。
- XAML 绑定默认编译期检查（`AvaloniaUseCompiledBindingsByDefault`），模板内跨层取 DataContext 用 `$parent[ItemsControl].((vm:MainWindowViewModel)DataContext)` 写法。Avalonia 12 主题只给 Slider/ScrollBar 等容器内嵌 Thumb 模板，**裸 `<Thumb>` 无默认模板→无可视子树→命中测试/拖拽全失效**，须自备 ControlTemplate（见 `MainWindow.axaml` 的 HistoryResizeThumb）。
