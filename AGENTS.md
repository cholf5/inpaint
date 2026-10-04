# AGENTS.md

lxfater/inpaint-web 的 C# / .NET 10 + Avalonia 桌面重写：MI-GAN 图片修复 + Real-ESRGAN ×4 高清化 + 导出压缩（PNG/JPEG/WebP），纯本地处理，无服务器、无 JS。

## 构建 / 运行

- 需要 .NET 10 SDK。`dotnet build Inpaint.slnx`；`dotnet run --project src/Inpaint.App`。
- 单元测试在 `tests/Inpaint.Tests`（xunit.v3 + Avalonia.Headless），`dotnet test` 运行；覆盖 Core 布局/遮罩转换、Inference 分块语义（`UpscaleEngine.FillTile`/`CopyTileCore` 为 internal，经 `InternalsVisibleTo` 供测试）与 App 层（ViewModel 生命周期、画布指针输入，`[AvaloniaFact]` 走 headless）。**需要 Avalonia 平台的参数化测试必须用 `[AvaloniaTheory]`**——裸 `[Theory]` 不走 headless 引导，`new WriteableBitmap` 直接报 "Unable to locate IPlatformRenderInterface"。不含需要模型文件或 ONNX session 的路径。**没有 .editorconfig / 格式化配置**——除测试外，验证手段就是编译通过加手动运行。CI（ubuntu）会跑同一套测试，两条已踩过的跨平台坑：像素断言不能采文本带（HistoryGraphView 标签 y≈94~106，Linux 次像素 AA/字体回退会给字形边缘染上彩边，采样点须选纯图形空白带）；测试不得假设真实用户 `settings.json` 已存在（全新机器/CI 上没有，快照前判存在性、结束时按原状删除或还原）。
- headless 测试入口 `TestAppBuilder` 必须用 `UseHeadlessDrawing = false` + `.UseSkia()`：headless 自绘位图的 `WriteableBitmap.Lock`/`CopyPixels` 语义与生产 Skia 不一致，会得到假结果。注意 App 命名空间与同名命名空间冲突（`Inpaint.App.App` 需别名）。
- macOS Dock 图标只来自 `.app` bundle 的 Info.plist（`CFBundleIconFile`）或运行时设 `NSApplication`，XAML `Window.Icon`/csproj `ApplicationIcon` 对它无效。开发期 `dotnet run` 是裸进程，由 `MacDockIcon`（libobjc 手发消息，失败静默）在桌面生命周期建立后设内嵌 icns 补上（对 bundle 是覆盖而非幂等）；正式包 `scripts/package-macos.sh [arm64|x64] [--fdd]` 产出 `artifacts/macos/Inpaint.app`（自包含、ad-hoc 签名，版本读 csproj `<Version>`），换 icns 后 Dock 有缓存需 `touch` bundle 或重启 Dock。图标圆角烤在素材里（Apple 模板：1024 画布、824 身、185.4 圆角）：Tahoe 会给 Finder 里直角 icns 自动蒙圆角，但 `setApplicationIconImage` 运行时图标和 Windows `.ico` 都不走蒙版；改图标先改 `app-icon.png` 母版，再用 4x 超采样圆角蒙版出 PNG，icns 走 `iconutil`、ico 走 PIL 多尺寸重生成。
- 发版：`scripts/release.sh x.y.z [--skip-test] [--watch]`——校验（main、工作树干净、三段数字版本、tag 不冲突）→ 本地 `dotnet test` → 改 csproj `<Version>` 提交 → 打 `v` tag push → CI（dotnet-desktop.yml）测试 + macOS/Windows/Linux 打包（Windows 另出 Inno Setup 安装包，共 5 个产物）+ 建 GitHub Release；`--watch` 轮询 CI 并核对 Release 产物（需 `gh` 已登录）。Agent 收到「发版 x.y.z」即跑该脚本（带 `--watch`），成功后向用户汇报 Release 链接与产物清单；失败按脚本 stderr 处理，CI 红则看 Actions 日志，修复后删远端/本地 tag（`git push origin :refs/tags/vX`、`git tag -d vX`）再重跑。tag 与 csproj 版本不一致会被 workflow 拒绝，重发同版本必须先删 tag。

## 目录与分层

- `inpaint-web/`：原网页版（React/TS/Vite）参考实现，**不在 .NET 解决方案内、未被 git 跟踪**。移植语义（遮罩 markProcess、分块 tileProc、下载 ensureModel）以它为基准；不要修改或提交它。
- 依赖只允许向下：`Inpaint.Core`（零包引用，图像布局转换）← `Inpaint.Inference`（仅 ONNX Runtime）← `Inpaint.App`（Avalonia + CommunityToolkit.Mvvm）。

## 关键模型语义（移植自网页版，改动前先对照 inpaint-web/src/utils.ts）

- **mask 张量：0 = 待修复，255 = 保留**。UI 白色笔触映射为 0：画布写入 `MaskLayer`（权威数据 = 每像素 1 字节灰度，255 = 待修复），推理经 `ImageProcessing.MaskGrayToChw`（255 → 0）转换；语义与旧版从 BGRA 位图按 OpenCV 灰度权重提纯白等价。
- **遮罩分两层（`Controls/MaskLayer`）**：权威 `Data`（byte[]，全分辨率）+ 显示 `Overlay`（WriteableBitmap，长边 ≤ 2048）。涂抹同步写两层；overlay 必须与原图分辨率解耦——WriteableBitmap 涂抹失效后整张重传 GPU（无增量更新），全分辨率遮罩在大图上等于每帧上传数百 MB。画布 `PaintDisc` 与 VM 推理（`MaskLayer.Data` 直转 CHW）都走它。
- MI-GAN（`migan_pipeline_v2.onnx`）：输入 image `[1,3,H,W]` uint8（RGB）+ mask `[1,1,H,W]` uint8，前后处理都在模型内完成。
- Real-ESRGAN（`realesrgan-x4.onnx`）：输入 float 0..1 RGB CHW；64×64 tile、四周外扩 6px 重叠、越界钳制到边缘像素，核心区 52×52，输出 ×4。
- 位图侧统一 Bgra8888 紧凑布局，模型侧 RGB CHW（平面式）；转换全部在 Core。
- **超大图性能约束**：像素级大块工作（`new Bitmap(stream)` 解码、`ExtractBgra`、CHW 前后处理、`CreateBitmap`）一律放后台（`Task.Run`），UI 线程只留属性赋值与缩略图绘制；修复/超分有像素上限 guard（VM `InpaintMaxPixels`/`UpscaleMaxPixels`，超限明确报错不 OOM）；超分输出超 1 亿像素（VM `UpscaleConfirmPixels`，按 ×4 后输出计）时先经 VM `ConfirmUpscaleAsync` 回调弹模态确认窗 `ConfirmWindow`（默认焦点「取消」，未接线按取消处理）再执行；历史裁剪除 `MaxHistory` 节点数外还有总字节预算（VM `HistoryByteBudget`，超大图自动收缩保留张数）；空闲悬停不触发画布整帧重绘（画笔光标环不可见时跳过 `InvalidateVisual`）。

## 导出压缩（Services/ImageExporter）

- 保存=导出：工具栏「导出…」/历史节点右键先弹模态 `ExportWindow`（格式 PNG/JPEG/WebP + 质量滑块 + **实时预估大小** + **1:1 取样预览**），确认后经 VM `ExportDialogProvider` 把 `ExportChoice`（含**编码好的字节**）带回，文件选择器按所选格式过滤扩展名。预估即编码——`ExportViewModel` 防抖 400ms 后整图编码一次，确认时参数命中缓存直接落盘不再重复编码；缓存是**单条目**（大图每份编码数 MB，不按质量档囤积），收益场景是估算在跑时切回上一组参数秒恢复，迟到的过期结果靠 generation 代数检查丢弃。上次导出格式+质量持久化在共享 `AppSettings`（`LastExportFormat`/`LastExportQuality`，`Sanitize` 收敛区间）：对话框环节独立为 VM `ShowExportDialogAsync`（初始值取设置、确认写回即随任何设置变更落盘，重启后仍是初始值），保存目录涉及隐私/换机路径失效仍只在内存；拆这个缝是因为 Avalonia 12 的 `IStorageProvider` 带 `NotClientImplementable` 哨兵成员，用户代码造不出替身。
- **1:1 取样预览 + 全图导航器**：预估完成后把真实字节解码回位图（`PreviewResult`，先换引用再 Dispose 旧图，Detach 清空），预览=落盘内容（JPEG 白底合成也如实可见）。交互：上窗 1:1 取样——按住=切原图对比、拖动=微调取样中心（`CalculateCropTranslate` 钳制到图像边缘、图小于视口整体居中）；下窗全图导航器（原图渲染、打开即有内容）——高亮框标出取样区在整图的位置，按下即跳转、拖动跟随（`CalculateThumbLayout` 信箱布局不放大超过 1:1、`ThumbPointToImage` 坐标映射）。**四条 Avalonia 渲染坑（探针实测）**：① Image 控件自身会裁掉超出 Bounds 的绘制，必须设 Width/Height=位图像素尺寸让其铺满，再由 Border 的**显式 `Clip`**（RectangleGeometry）裁出取样窗；② `ClipToBounds` 不行——它的裁剪发生在子项自身坐标系、会跟着 RenderTransform 一起移动，负平移直接把可见区移没；③ 显式尺寸的子项在 Panel 里默认**居中**摆放（Stretch 对齐 + 显式宽高 → 居中），会叠加 ((槽宽-宽)/2, …) 的基底偏移把平移后的内容推出视口，须 Left/Top 对齐；④ `Stretch.Fill` + 显式像素尺寸强制 1 图像像素=1 DIP 的真 1:1，不受文件 DPI 元数据影响。
- 编码器用 Avalonia 自带的 Skia（`SKImage.Encode`，libpng/libjpeg-turbo/libwebp），**零新增原生依赖**；SkiaSharp 经 Avalonia 传递引用，勿再显式加包。**alpha 约定（探针实测）**：Avalonia 解码的位图缓冲是**预乘 alpha**（且 PNG 源解码为 Rgba8888，`ImageExporter.ExtractBgra` 统一转紧凑 BGRA 并交换红蓝），编码时按 `SKAlphaType.Premul` 声明；历史树新生成的位图全不透明，两者一致。JPEG 无 alpha：编码前把非不透明像素合成到白底（预乘公式 `c + 255 - a`，半透明红叠白底=粉色不是纯红），全不透明走零拷贝快路径；PNG/WebP 保留 alpha。质量参数 PNG 忽略（键里也不参与，来回调质量命中同一份缓存）。
- 无 UI 环境（单测/CI）时 `_storage` 为 null 直接返回；`ExportDialogProvider` 未接线视为取消（安全兜底，同 `ConfirmUpscaleAsync` 先例）。`OpenWriteAsync` 不截断旧文件，覆盖前须 `SetLength(0)`。大图 WebP 编码要数秒——预估全程后台可取消，状态行「估算中…」；`EstimateExecutor` 替身供单测注入（真实编码语义另测），`EstimateDebounce` 置零 + `await vm.EstimateTask` 是测试的标准等待姿势。

## 推理与模型缓存

- 模型首次使用时由 `ModelStore` 下载（HuggingFace 主源 + CDN 备源，临时文件原子替换），缓存到应用数据目录：macOS `~/Library/Application Support/Inpaint/models/`（Windows `%APPDATA%\Inpaint`、Linux `~/.local/share/Inpaint`）。网络受限时设 `https_proxy` 或手动放入文件。
- EP 选择在 `OrtConfig.MakeSessionOptions(allowCoreML, mode)`，按引擎区分：超分（全卷积）macOS 默认 CoreML——M2 实测 64×64 tile 540ms(CPU)→11ms，会话编译 3~4 秒一次性开销；修复（MI-GAN）保持 CPU——CoreML 只能接管其 559 个节点中的 375 个，分区搬运使单次推理 0.4s 恶化到 69s。超分设备可由设置界面选择（`AccelerationMode`，经 `UpscaleEngine` 传入，会话懒创建故只对下次会话生效，设备变更时 ViewModel 丢弃已建会话、繁忙则推迟到空闲）；`INPAINT_EP` 环境变量优先级最高：`cpu` 强制全部回退 CPU，`coreml` 强制启用（MI-GAN 上极慢，仅实验用）。Windows DML 需加 `Microsoft.ML.OnnxRuntime.DirectML` 包并在同一处追加 EP。
- 输入/输出张量名从 session metadata 探测，带兜底默认值（超分 `"input.1"` / `"1895"`）。

## 代码约定

- 全部项目 `net10.0` + ImplicitUsings + Nullable；注释、XML doc 用中文。**用户可见 UI 字符串一律经 `Translations.Instance`**（zh 源词典 + en 覆盖，键 = 属性名用 `nameof` 对齐；新增字符串两份词典都要补，XAML 绑定 `{Binding X, Source={x:Static loc:Translations.Instance}}`），默认简体中文。

## 设置与本地化

- 设置运行期是共享单实例 `AppSettings`（App 启动 `SettingsService.Load()` 后传给 MainWindow/ViewModel/设置窗口）：主题、语言、Real-ESRGAN 设备（`AccelerationMode`）、默认画笔大小、生成历史上限、涂抹松手立即修复（`InpaintOnStrokeRelease`，默认开、与 Web 版一致，可关回按钮式——开启时画布 `StrokeCommitted` 事件经 VM `OnStrokeCommitted` 自动执行修复，门槛同 Enter 快捷键，工具栏隐藏修复按钮；清除涂抹按钮无状态化：仅画布残留未处理涂抹且非繁忙时显示（`ShowClearMask`），失败遮罩滞留时才浮现作逃生门；测试经 VM `AutoInpaintExecutor` 替身避免真实推理）、启动时检查更新（`CheckUpdateOnStartup`，默认关——纯本地应用，启动联网必须 opt-in）。任何属性变更即时生效并落盘——主题/语言由 `App` 订阅应用，设备由主 ViewModel 订阅重建超分引擎。SettingsWindow 只是编辑视图（`SettingsViewModel` 用跨语言稳定的 `OptionItem` 实例数组 + SelectedIndex 映射枚举，切语言只改 Label、不重建 ItemsSource，XAML 须配 ItemTemplate 绑定 Label；重建式换文案会异步清空 ComboBox 选区并把旧选中项经双向绑定推回），不拥有持久化。窗口**非模态、单实例**：`MainWindow.OpenSettings()` 用 `Show(owner)` 打开（Owner 是 protected 只能走该重载），重复打开只 `Activate` 置前，`Closed` 清引用以便重开。内容是 TabControl 五页签：通用（主题/语言/默认画笔/历史上限/松手即修复/启动检查更新）、性能、模型、快捷键、关于（版本取 `Inpaint.App` 程序集、作者主页链接经 `Launcher.LaunchUriAsync` 打开、底部「检查更新」按钮 + 结果状态行 + 发现新版时的 Release 页跳转按钮）；**页签内容按选中实例化**，未选中页签的控件不在可视树（绑定仍活跃），涉及具体页签控件的测试须先切页签。快捷键键位语义（跨平台坑，探针级核对过 Avalonia 源码）：`KeyGesture.Matches` 对修饰键**精确相等**，且 macOS 上 ⌘ 是 `Meta`、与物理 `Ctrl` 互不匹配——XAML `KeyBindings` 里的 `Ctrl+O/S/Z` 在 mac 上只认物理 Ctrl，故 MainWindow 构造时按 `OperatingSystem.IsMacOS()` 补 ⌘=Meta 绑定（缩放 ±/0/1 与 ⌘V 粘贴走 OnKeyDown 的 `Control||Meta` 双认，本就两键皆可）；设置页快捷键键帽随平台取 `SettingsViewModel` 静态键帽属性（x:Static，不走 Translations）；画笔 `[` `]` 另有 `-`/`=` 备用键（OnKeyDown 处理、含 Shift 变体以兼容 AZERTY 的 +，命令本身无 CanExecute 门控）。
- 检查更新 `Services/UpdateChecker`：查 GitHub latest Release（必须带 User-Agent 否则 403，未认证限流 60 次/小时/IP）；tag 去 `v` 前缀按三段数值比较，基准 = `UpdateChecker.CurrentVersion`（`Inpaint.App` 程序集版本，csproj `<Version>`，发版改这里；GitHub tag 带 `v` 前缀）。不自定义 HttpMessageHandler（默认读 `https_proxy` 环境变量，与模型下载代理兜底一致）；失败统一转 `UpdateCheckResult(Failed)` 不向 UI 抛。手动检查结果写「关于」页状态行（瞬态文本，保持出现时语言），启动检查（opt-in）结果写主窗口 StatusText 且失败静默；测试经 `UpdateChecker(HttpMessageHandler, currentVersion)` 注入假响应，不打真实网络。
- `SettingsService`：settings.json 与模型缓存同目录（`…/Inpaint/settings.json`），枚举存名字、临时文件原子替换；文件缺失/损坏回退默认值，数值越界收敛到合法区间（手改文件兜底）。`Load/Save` 的 path 参数供单测注入临时路径。
- 语言切换 = `Translations.SetLanguage` 逐属性 raise PropertyChanged（静态字段按声明顺序初始化，**词典必须先于 `Instance`**）；StatusText 等瞬态文本与历史节点标题不回溯刷新（无瞬态状态时的初始提示经 VM 派生属性 StatusDisplay 随语言刷新）。测试断言中文字符串的类须在构造函数固定 `SetLanguage(SimplifiedChinese)`，且程序集已禁用集合并行（Translations 是进程级单例）；切语言会牵动 headless App 启动时创建的 MainWindow 绑定，相关测试须走 `[AvaloniaFact]`（UI 线程），普通 `[Fact]` 里切语言会跨线程崩溃。
- App 层 MVVM 用 CommunityToolkit.Mvvm 源生成器：`[ObservableProperty]`、`[RelayCommand(CanExecute=...)]` + `NotifyCanExecuteChangedFor`；命令可用性统一由 `IsBusy` gate。
- 耗时工作 `Task.Run` 下放线程池，UI 更新走 `IProgress<T>`；用户可见错误写入 `StatusText`，不向 UI 抛异常。
- 像素级位图访问用 unsafe 指针（App 已开 AllowUnsafeBlocks）。**WriteableBitmap 有 RowBytes stride，逐行拷贝必须用它**，参见 `MainWindowViewModel.CreateBitmap`、`ImageExporter.ExtractBgra`、`ImageEditorControl.PaintDisc`；Core 的转换函数则假设紧凑无 padding。
- Bitmap 生命周期：生成历史是 git 式分叉树（`ImageHistoryNode`，撤销后生成即分叉；撤销/回到原图=在树上移动当前节点），节点里的 Bitmap/Thumbnail 由 ViewModel 统一 Dispose（`AdoptBitmap`/`PushHistory`/`PruneHistory`），新增产生位图的路径注意别泄漏；节点上限默认 25（`AppSettings.MaxHistory`，设置界面可调），超限优先丢弃最旧的非当前分支，原图（根）永不丢弃。
- 历史面板是整图自绘的竖向 git Graph（`HistoryGraphView`，无列表）：车道为纵向列（0=原图主干），行按创建时间从上往下；**第一子节点延续父车道，基于中间节点生成的新子节点开右侧新车道（原车道不动）**，车道序按分叉发生顺序分配。布局字段（LaneIndex/RowIndex）由 `RebuildHistory` 统一重算，控件只负责绘制/命中/右键菜单。
- XAML 绑定默认编译期检查（`AvaloniaUseCompiledBindingsByDefault`），模板内跨层取 DataContext 用 `$parent[ItemsControl].((vm:MainWindowViewModel)DataContext)` 写法。Avalonia 12 主题只给 Slider/ScrollBar 等容器内嵌 Thumb 模板，**裸 `<Thumb>` 无默认模板→无可视子树→命中测试/拖拽全失效**，须自备 ControlTemplate（见 `MainWindow.axaml` 的 HistoryResizeThumb）。
