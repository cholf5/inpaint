using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Inpaint.App.Services;

namespace Inpaint.App.Localization;

/// <summary>
/// 全部用户可见 UI 字符串的单例字典：zh 为源词典（键 = 属性名），en 只放覆盖项，缺键自动回退中文。
/// XAML 绑定写法：{Binding Open, Source={x:Static loc:Translations.Instance}}（编译期检查属性名）。
/// 切换语言时遍历所有属性名逐个 raise PropertyChanged，让全部绑定刷新；
/// StatusText 等瞬态文本与历史节点标题不回溯刷新，保持出现/创建时的语言
/// （无瞬态状态时的初始提示 InitialStatus 例外，经 VM 派生属性随语言刷新）。
/// </summary>
public sealed class Translations : INotifyPropertyChanged
{
    // 静态字段按声明顺序初始化：词典必须先于 Instance（其构造要用到词典）
    private static readonly string[] AllPropertyNames =
        typeof(Translations).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

    private static readonly Dictionary<string, string> Zh = new()
    {
        [nameof(AppTitle)] = "Inpaint — 图片修复与高清化",
        [nameof(Open)] = "打开图片…",
        [nameof(Save)] = "保存 PNG",
        [nameof(Undo)] = "撤销",
        [nameof(ResetToOriginal)] = "回到原图",
        [nameof(History)] = "历史",
        [nameof(Brush)] = "画笔",
        [nameof(ClearMask)] = "清除涂抹",
        [nameof(Inpaint)] = "修复涂抹区域",
        [nameof(Upscale)] = "高清放大 ×4",
        [nameof(Settings)] = "设置",
        [nameof(DropHint)] = "把图片拖进窗口，或点击「{0}」",
        [nameof(HistoryTitleFormat)] = "生成历史（{0}）",
        [nameof(HistoryEmptyHint)] = "打开或生成图片后，这里会记录每一次修复与放大",
        [nameof(FitWindow)] = "适应窗口",
        [nameof(ActualSize)] = "实际大小",
        [nameof(ZoomIn)] = "放大",
        [nameof(ZoomOut)] = "缩小",
        [nameof(InitialStatus)] = "打开一张图片，涂抹掉不想要的内容",
        [nameof(CannotOpen)] = "无法打开图片：{0}",
        [nameof(LoadingImage)] = "正在加载图片…",
        [nameof(OriginalNode)] = "原图",
        [nameof(LoadedStatus)] = "已加载 {0}×{1}，涂抹后点「{2}」",
        [nameof(LoadedStatusAuto)] = "已加载 {0}×{1}，涂抹后松手即自动修复",
        [nameof(PreparingInpaint)] = "正在准备修复模型…",
        [nameof(Inpainting)] = "正在修复…",
        [nameof(InpaintDone)] = "修复完成（{0}×{1}）",
        [nameof(InpaintFailed)] = "修复失败：{0}",
        [nameof(InpaintTooLarge)] = "图片过大（{0}×{1}），超出修复模型处理上限，无法修复",
        [nameof(PreparingUpscale)] = "正在准备超分模型…",
        [nameof(Upscaling)] = "正在放大 {0}×{1} → {2}×{3}…",
        [nameof(UpscaleDone)] = "放大完成（{0}×{1}）",
        [nameof(UpscaleFailed)] = "放大失败：{0}",
        [nameof(UpscaleTooLarge)] = "图片过大（{0}×{1}），超出超分模型处理上限，无法放大",
        // 文案中的「1 亿像素」须与 MainWindowViewModel.UpscaleConfirmPixels 默认值（100_000_000）保持一致
        [nameof(UpscaleConfirmTitle)] = "确认高清放大",
        [nameof(UpscaleConfirmLarge)] =
            "即将把图片从 {0}×{1} 放大到 {2}×{3}。输出超过 1 亿像素，结果位图与推理的峰值内存可能达数 GB，" +
            "低内存机器可能出现系统级卡顿。确定继续吗？",
        [nameof(UpscaleConfirmContinue)] = "继续放大",
        [nameof(UpscaleConfirmCancel)] = "取消",
        [nameof(UpscaleCancelled)] = "已取消放大",
        [nameof(SwitchedToNode)] = "已切换到「{0}」，继续修复/放大将开新车道分叉",
        [nameof(Undone)] = "已撤销",
        [nameof(BackToOriginal)] = "已回到原图",
        [nameof(CopiedToClipboard)] = "已复制「{0}」到剪贴板",
        [nameof(CopyFailed)] = "复制失败：{0}",
        [nameof(MaskCleared)] = "已清除涂抹",
        [nameof(Saved)] = "已保存：{0}",
        [nameof(SaveFailed)] = "保存失败：{0}",
        [nameof(DownloadingModel)] = "正在下载模型 {0:F0}%（首次使用需下载，之后有本地缓存）",
        [nameof(NodeInpaint)] = "修复",
        [nameof(NodeUpscale)] = "放大 ×4",
        [nameof(PickerOpenTitle)] = "打开图片",
        [nameof(PickerSaveTitle)] = "保存图片",
        [nameof(FileTypeImages)] = "图片",
        [nameof(PngFileType)] = "PNG 图片",
        [nameof(CopyImage)] = "复制图片",
        [nameof(DownloadPng)] = "下载 PNG",
        [nameof(SettingsTitle)] = "设置",
        [nameof(SectionGeneral)] = "通用",
        [nameof(ThemeLabel)] = "主题",
        [nameof(FollowSystem)] = "跟随系统",
        [nameof(ThemeLight)] = "浅色",
        [nameof(ThemeDark)] = "深色",
        [nameof(LanguageLabel)] = "语言",
        [nameof(LangChinese)] = "简体中文",
        [nameof(LangEnglish)] = "English",
        [nameof(SectionPerformance)] = "性能",
        [nameof(DeviceLabel)] = "Real-ESRGAN 执行设备",
        [nameof(DeviceAuto)] = "自动（推荐）",
        [nameof(DeviceCpu)] = "CPU",
        [nameof(DeviceGpu)] = "GPU（CoreML）",
        [nameof(DeviceHint)] =
            "GPU 经 CoreML 加速，目前仅 macOS 生效；切换后首次推理需重新编译模型（约 3~4 秒）。" +
            "修复模型（MI-GAN）固定使用 CPU。环境变量 INPAINT_EP 优先级高于此设置。",
        [nameof(DefaultBrushLabel)] = "默认画笔大小",
        [nameof(MaxHistoryLabel)] = "生成历史上限",
        [nameof(MaxHistoryHint)] = "超出上限时优先丢弃最旧的不在当前路径上的节点，原图永不丢弃。",
        [nameof(SectionShortcuts)] = "快捷键",
        [nameof(ShortcutBrushDecrease)] = "缩小画笔",
        [nameof(ShortcutBrushIncrease)] = "放大画笔",
        [nameof(ShortcutBrushWheel)] = "画布上滚动滚轮调节画笔大小",
        [nameof(ShortcutInpaint)] = "执行修复（需已涂抹）",
        [nameof(ShortcutZoomWheel)] = "画布上 ⌘/Ctrl/Alt+滚轮：以光标为中心缩放",
        [nameof(ShortcutZoomKeys)] = "⌘/Ctrl 加/减号步进缩放；⌘/Ctrl+0 适应窗口；⌘/Ctrl+1 实际大小",
        [nameof(ShortcutPan)] = "按住空格拖拽（或中键拖拽）平移画布",
        [nameof(SectionModels)] = "模型",
        [nameof(ModelsDirLabel)] = "模型缓存目录",
        [nameof(OpenModelsFolder)] = "打开模型文件夹",
        [nameof(OpenFolderFailed)] = "打开文件夹失败：{0}",
        [nameof(SectionAbout)] = "关于",
        [nameof(AboutIntro)] =
            "纯本地运行的图片修复与高清化工具：MI-GAN 涂抹修复 + Real-ESRGAN ×4 高清放大，" +
            "推理全程在本机 ONNX Runtime 完成，图片不上传任何服务器。",
        [nameof(AuthorLabel)] = "作者",
        [nameof(AuthorName)] = "周尔复",
        [nameof(LicenseLabel)] = "开源许可",
        [nameof(RepoLabel)] = "仓库地址",
        [nameof(CheckUpdateOnStartupLabel)] = "启动时检查更新",
        [nameof(InpaintOnStrokeReleaseLabel)] = "涂抹后松手立即修复",
        [nameof(InpaintOnStrokeReleaseHint)] =
            "一笔涂抹松开鼠标即自动执行修复（与 Web 版一致）；关闭后回到点「修复涂抹区域」按钮的操作方式。" +
            "「清除涂抹」仅在画布上残留未处理的涂抹时出现（如修复失败后），不随本开关变化。",
        [nameof(CheckUpdateHint)] =
            "默认关闭以保持纯本地使用；开启后启动时仅向 GitHub 查询最新版本号，不上传图片等任何数据。",
        [nameof(CheckUpdateButton)] = "检查更新",
        [nameof(CheckingUpdate)] = "正在检查更新…",
        [nameof(UpdateAvailableStatus)] = "发现新版本 {0}",
        [nameof(UpToDateStatus)] = "已是最新版本（{0}）",
        [nameof(UpdateCheckFailed)] = "检查更新失败：{0}",
        [nameof(OpenReleasePage)] = "前往 Release 页",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        [nameof(AppTitle)] = "Inpaint — Inpainting & Upscaling",
        [nameof(Open)] = "Open Image…",
        [nameof(Save)] = "Save PNG",
        [nameof(Undo)] = "Undo",
        [nameof(ResetToOriginal)] = "Reset to Original",
        [nameof(History)] = "History",
        [nameof(Brush)] = "Brush",
        [nameof(ClearMask)] = "Clear Mask",
        [nameof(Inpaint)] = "Inpaint Painted Areas",
        [nameof(Upscale)] = "Upscale ×4",
        [nameof(Settings)] = "Settings",
        [nameof(DropHint)] = "Drag an image into the window, or click “{0}”",
        [nameof(HistoryTitleFormat)] = "History ({0})",
        [nameof(HistoryEmptyHint)] =
            "Every inpaint and upscale will be recorded here once an image is opened or generated",
        [nameof(FitWindow)] = "Fit Window",
        [nameof(ActualSize)] = "Actual Size",
        [nameof(ZoomIn)] = "Zoom In",
        [nameof(ZoomOut)] = "Zoom Out",
        [nameof(InitialStatus)] = "Open an image, then paint over the content you want removed",
        [nameof(CannotOpen)] = "Could not open image: {0}",
        [nameof(LoadingImage)] = "Loading image…",
        [nameof(OriginalNode)] = "Original",
        [nameof(LoadedStatus)] = "Loaded {0}×{1}; paint over unwanted areas, then click “{2}”",
        [nameof(LoadedStatusAuto)] = "Loaded {0}×{1}; paint over unwanted areas — release to inpaint",
        [nameof(PreparingInpaint)] = "Preparing inpaint model…",
        [nameof(Inpainting)] = "Inpainting…",
        [nameof(InpaintDone)] = "Inpaint done ({0}×{1})",
        [nameof(InpaintFailed)] = "Inpaint failed: {0}",
        [nameof(InpaintTooLarge)] = "Image too large ({0}×{1}) for the inpaint model; unable to inpaint",
        [nameof(PreparingUpscale)] = "Preparing upscale model…",
        [nameof(Upscaling)] = "Upscaling {0}×{1} → {2}×{3}…",
        [nameof(UpscaleDone)] = "Upscale done ({0}×{1})",
        [nameof(UpscaleFailed)] = "Upscale failed: {0}",
        [nameof(UpscaleTooLarge)] = "Image too large ({0}×{1}) for the upscale model; unable to upscale",
        [nameof(UpscaleConfirmTitle)] = "Confirm Upscale",
        [nameof(UpscaleConfirmLarge)] =
            "The image is about to be upscaled from {0}×{1} to {2}×{3}. The output exceeds 100 megapixels — " +
            "the result bitmap and inference may peak at several GB of memory and can make the whole system " +
            "unresponsive on low-memory machines. Continue anyway?",
        [nameof(UpscaleConfirmContinue)] = "Upscale Anyway",
        [nameof(UpscaleConfirmCancel)] = "Cancel",
        [nameof(UpscaleCancelled)] = "Upscale cancelled",
        [nameof(SwitchedToNode)] = "Switched to “{0}”; further edits will fork into a new lane",
        [nameof(Undone)] = "Undone",
        [nameof(BackToOriginal)] = "Back to the original image",
        [nameof(CopiedToClipboard)] = "Copied “{0}” to clipboard",
        [nameof(CopyFailed)] = "Copy failed: {0}",
        [nameof(MaskCleared)] = "Mask cleared",
        [nameof(Saved)] = "Saved: {0}",
        [nameof(SaveFailed)] = "Save failed: {0}",
        [nameof(DownloadingModel)] =
            "Downloading model {0:F0}% (downloaded on first use, cached locally afterwards)",
        [nameof(NodeInpaint)] = "Inpaint",
        [nameof(NodeUpscale)] = "Upscale ×4",
        [nameof(PickerOpenTitle)] = "Open Image",
        [nameof(PickerSaveTitle)] = "Save Image",
        [nameof(FileTypeImages)] = "Image",
        [nameof(PngFileType)] = "PNG Image",
        [nameof(CopyImage)] = "Copy Image",
        [nameof(DownloadPng)] = "Download PNG",
        [nameof(SettingsTitle)] = "Settings",
        [nameof(SectionGeneral)] = "General",
        [nameof(ThemeLabel)] = "Theme",
        [nameof(FollowSystem)] = "Follow system",
        [nameof(ThemeLight)] = "Light",
        [nameof(ThemeDark)] = "Dark",
        [nameof(LanguageLabel)] = "Language",
        [nameof(LangChinese)] = "简体中文",
        [nameof(LangEnglish)] = "English",
        [nameof(SectionPerformance)] = "Performance",
        [nameof(DeviceLabel)] = "Real-ESRGAN device",
        [nameof(DeviceAuto)] = "Auto (recommended)",
        [nameof(DeviceCpu)] = "CPU",
        [nameof(DeviceGpu)] = "GPU (CoreML)",
        [nameof(DeviceHint)] =
            "GPU runs via CoreML and is currently macOS-only; the first inference after switching " +
            "recompiles the model (3–4 s). The inpaint model (MI-GAN) always runs on CPU. " +
            "The INPAINT_EP environment variable takes precedence over this setting.",
        [nameof(DefaultBrushLabel)] = "Default brush size",
        [nameof(MaxHistoryLabel)] = "History limit",
        [nameof(MaxHistoryHint)] =
            "When exceeded, the oldest nodes off the current path are pruned first; " +
            "the original image is never dropped.",
        [nameof(SectionShortcuts)] = "Shortcuts",
        [nameof(ShortcutBrushDecrease)] = "Shrink brush",
        [nameof(ShortcutBrushIncrease)] = "Grow brush",
        [nameof(ShortcutBrushWheel)] = "Scroll over the canvas to resize brush",
        [nameof(ShortcutInpaint)] = "Inpaint painted areas (paint the mask first)",
        [nameof(ShortcutZoomWheel)] = "Zoom centered on the pointer: ⌘/Ctrl/Alt + scroll over the canvas",
        [nameof(ShortcutZoomKeys)] =
            "⌘/Ctrl + plus/minus zooms; ⌘/Ctrl+0 fits the window; ⌘/Ctrl+1 shows actual size",
        [nameof(ShortcutPan)] = "Hold Space and drag (or drag with the middle button) to pan the canvas",
        [nameof(SectionModels)] = "Models",
        [nameof(ModelsDirLabel)] = "Model cache folder",
        [nameof(OpenModelsFolder)] = "Open Models Folder",
        [nameof(OpenFolderFailed)] = "Could not open folder: {0}",
        [nameof(SectionAbout)] = "About",
        [nameof(AboutIntro)] =
            "A fully local image inpainting & upscaling tool: MI-GAN inpainting + Real-ESRGAN ×4 upscaling. " +
            "All inference runs on-device via ONNX Runtime — images never leave your machine.",
        [nameof(AuthorLabel)] = "Author",
        [nameof(AuthorName)] = "周尔复",
        [nameof(LicenseLabel)] = "License",
        [nameof(RepoLabel)] = "Repository",
        [nameof(CheckUpdateOnStartupLabel)] = "Check for updates at startup",
        [nameof(InpaintOnStrokeReleaseLabel)] = "Inpaint immediately on stroke release",
        [nameof(InpaintOnStrokeReleaseHint)] =
            "Release the mouse after a stroke to inpaint right away (as in the web version); turn off to go " +
            "back to clicking the “Inpaint Painted Areas” button. “Clear Mask” appears only while unprocessed " +
            "paint remains on the canvas (e.g. after a failure), regardless of this switch.",
        [nameof(CheckUpdateHint)] =
            "Off by default to keep the app fully local; when enabled, the launch check only asks " +
            "GitHub for the latest version number — no images or other data are uploaded.",
        [nameof(CheckUpdateButton)] = "Check for Updates",
        [nameof(CheckingUpdate)] = "Checking for updates…",
        [nameof(UpdateAvailableStatus)] = "New version available: {0}",
        [nameof(UpToDateStatus)] = "Up to date ({0})",
        [nameof(UpdateCheckFailed)] = "Update check failed: {0}",
        [nameof(OpenReleasePage)] = "Open Release Page",
    };

    public static Translations Instance { get; } = new();

    private Dictionary<string, string> _strings;

    private Translations() =>
        _strings = StringsFor(Resolve(AppLanguage.System, CultureInfo.CurrentUICulture));

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>当前生效语言（System 已解析为具体语言）。</summary>
    public AppLanguage CurrentLanguage { get; private set; }

    public void SetLanguage(AppLanguage language)
    {
        var resolved = Resolve(language, CultureInfo.CurrentUICulture);
        _strings = StringsFor(resolved);
        CurrentLanguage = resolved;
        foreach (var name in AllPropertyNames)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>跟随系统时按 UI 文化的两位语言码解析：zh → 简体中文，其余（含 en、ja）一律英文。internal 供单测。</summary>
    internal static AppLanguage Resolve(AppLanguage preference, CultureInfo uiCulture) =>
        preference switch
        {
            AppLanguage.SimplifiedChinese => AppLanguage.SimplifiedChinese,
            AppLanguage.English => AppLanguage.English,
            _ => uiCulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.SimplifiedChinese
                : AppLanguage.English,
        };

    private static Dictionary<string, string> StringsFor(AppLanguage language)
    {
        var merged = new Dictionary<string, string>(Zh);
        if (language != AppLanguage.SimplifiedChinese)
            foreach (var (key, value) in En)
                merged[key] = value;
        return merged;
    }

    private string Get([CallerMemberName] string? key = null) =>
        _strings.TryGetValue(key!, out var value) ? value : key!;

    // ---- 窗口 / 工具栏 ----

    public string AppTitle => Get();
    public string Open => Get();
    public string Save => Get();
    public string Undo => Get();
    public string ResetToOriginal => Get();
    public string History => Get();
    public string Brush => Get();
    public string ClearMask => Get();
    public string Inpaint => Get();
    public string Upscale => Get();
    public string Settings => Get();

    // ---- 画布与历史面板 ----

    /// <summary>空画布提示，{0} 为「打开图片…」按钮文案。</summary>
    public string DropHint => string.Format(Get(), Open);
    public string HistoryTitleFormat => Get();
    public string HistoryEmptyHint => Get();

    // ---- 画布缩放 ----

    public string FitWindow => Get();
    public string ActualSize => Get();
    public string ZoomIn => Get();
    public string ZoomOut => Get();

    // ---- 状态栏与历史节点标题 ----

    public string InitialStatus => Get();
    public string CannotOpen => Get();
    public string LoadingImage => Get();
    public string OriginalNode => Get();
    public string LoadedStatus => Get();
    public string LoadedStatusAuto => Get();
    public string PreparingInpaint => Get();
    public string Inpainting => Get();
    public string InpaintDone => Get();
    public string InpaintFailed => Get();
    public string InpaintTooLarge => Get();
    public string PreparingUpscale => Get();
    public string Upscaling => Get();
    public string UpscaleDone => Get();
    public string UpscaleFailed => Get();
    public string UpscaleTooLarge => Get();
    public string UpscaleConfirmTitle => Get();
    public string UpscaleConfirmLarge => Get();
    public string UpscaleConfirmContinue => Get();
    public string UpscaleConfirmCancel => Get();
    public string UpscaleCancelled => Get();
    public string SwitchedToNode => Get();
    public string Undone => Get();
    public string BackToOriginal => Get();
    public string CopiedToClipboard => Get();
    public string CopyFailed => Get();
    public string MaskCleared => Get();
    public string Saved => Get();
    public string SaveFailed => Get();
    public string DownloadingModel => Get();
    public string NodeInpaint => Get();
    public string NodeUpscale => Get();

    // ---- 文件对话框 ----

    public string PickerOpenTitle => Get();
    public string PickerSaveTitle => Get();
    public string FileTypeImages => Get();
    public string PngFileType => Get();

    // ---- 历史节点右键菜单 ----

    public string CopyImage => Get();
    public string DownloadPng => Get();

    // ---- 设置窗口 ----

    public string SettingsTitle => Get();
    public string SectionGeneral => Get();
    public string ThemeLabel => Get();
    public string FollowSystem => Get();
    public string ThemeLight => Get();
    public string ThemeDark => Get();
    public string LanguageLabel => Get();
    public string LangChinese => Get();
    public string LangEnglish => Get();
    public string SectionPerformance => Get();
    public string DeviceLabel => Get();
    public string DeviceAuto => Get();
    public string DeviceCpu => Get();
    public string DeviceGpu => Get();
    public string DeviceHint => Get();
    public string DefaultBrushLabel => Get();
    public string MaxHistoryLabel => Get();
    public string MaxHistoryHint => Get();
    public string SectionShortcuts => Get();
    public string ShortcutBrushDecrease => Get();
    public string ShortcutBrushIncrease => Get();
    public string ShortcutBrushWheel => Get();
    public string ShortcutInpaint => Get();
    public string ShortcutZoomWheel => Get();
    public string ShortcutZoomKeys => Get();
    public string ShortcutPan => Get();
    public string SectionModels => Get();
    public string ModelsDirLabel => Get();
    public string OpenModelsFolder => Get();
    public string OpenFolderFailed => Get();
    public string SectionAbout => Get();
    public string AboutIntro => Get();
    public string AuthorLabel => Get();
    public string AuthorName => Get();
    public string LicenseLabel => Get();
    public string RepoLabel => Get();
    public string InpaintOnStrokeReleaseLabel => Get();
    public string InpaintOnStrokeReleaseHint => Get();

    // ---- 检查更新 ----

    /// <summary>瞬态检查结果（状态行）不经此刷新，保持出现时的语言。</summary>
    public string CheckUpdateOnStartupLabel => Get();
    public string CheckUpdateHint => Get();
    public string CheckUpdateButton => Get();
    public string CheckingUpdate => Get();
    public string UpdateAvailableStatus => Get();
    public string UpToDateStatus => Get();
    public string UpdateCheckFailed => Get();
    public string OpenReleasePage => Get();
}
