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
        [nameof(InitialStatus)] = "打开一张图片，涂抹掉不想要的内容",
        [nameof(CannotOpen)] = "无法打开图片：{0}",
        [nameof(OriginalNode)] = "原图",
        [nameof(LoadedStatus)] = "已加载 {0}×{1}，涂抹后点「{2}」",
        [nameof(PreparingInpaint)] = "正在准备修复模型…",
        [nameof(Inpainting)] = "正在修复…",
        [nameof(InpaintDone)] = "修复完成（{0}×{1}）",
        [nameof(InpaintFailed)] = "修复失败：{0}",
        [nameof(PreparingUpscale)] = "正在准备超分模型…",
        [nameof(Upscaling)] = "正在放大 {0}×{1} → {2}×{3}…",
        [nameof(UpscaleDone)] = "放大完成（{0}×{1}）",
        [nameof(UpscaleFailed)] = "放大失败：{0}",
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
        [nameof(SectionAppearance)] = "外观",
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
        [nameof(SectionGeneral)] = "常规",
        [nameof(DefaultBrushLabel)] = "默认画笔大小",
        [nameof(MaxHistoryLabel)] = "生成历史上限",
        [nameof(MaxHistoryHint)] = "超出上限时优先丢弃最旧的不在当前路径上的节点，原图永不丢弃。",
        [nameof(SectionModels)] = "模型",
        [nameof(ModelsDirLabel)] = "模型缓存目录",
        [nameof(OpenModelsFolder)] = "打开模型文件夹",
        [nameof(OpenFolderFailed)] = "打开文件夹失败：{0}",
        [nameof(Close)] = "关闭",
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
        [nameof(InitialStatus)] = "Open an image, then paint over the content you want removed",
        [nameof(CannotOpen)] = "Could not open image: {0}",
        [nameof(OriginalNode)] = "Original",
        [nameof(LoadedStatus)] = "Loaded {0}×{1}; paint over unwanted areas, then click “{2}”",
        [nameof(PreparingInpaint)] = "Preparing inpaint model…",
        [nameof(Inpainting)] = "Inpainting…",
        [nameof(InpaintDone)] = "Inpaint done ({0}×{1})",
        [nameof(InpaintFailed)] = "Inpaint failed: {0}",
        [nameof(PreparingUpscale)] = "Preparing upscale model…",
        [nameof(Upscaling)] = "Upscaling {0}×{1} → {2}×{3}…",
        [nameof(UpscaleDone)] = "Upscale done ({0}×{1})",
        [nameof(UpscaleFailed)] = "Upscale failed: {0}",
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
        [nameof(SectionAppearance)] = "Appearance",
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
        [nameof(SectionGeneral)] = "General",
        [nameof(DefaultBrushLabel)] = "Default brush size",
        [nameof(MaxHistoryLabel)] = "History limit",
        [nameof(MaxHistoryHint)] =
            "When exceeded, the oldest nodes off the current path are pruned first; " +
            "the original image is never dropped.",
        [nameof(SectionModels)] = "Models",
        [nameof(ModelsDirLabel)] = "Model cache folder",
        [nameof(OpenModelsFolder)] = "Open Models Folder",
        [nameof(OpenFolderFailed)] = "Could not open folder: {0}",
        [nameof(Close)] = "Close",
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

    // ---- 状态栏与历史节点标题 ----

    public string InitialStatus => Get();
    public string CannotOpen => Get();
    public string OriginalNode => Get();
    public string LoadedStatus => Get();
    public string PreparingInpaint => Get();
    public string Inpainting => Get();
    public string InpaintDone => Get();
    public string InpaintFailed => Get();
    public string PreparingUpscale => Get();
    public string Upscaling => Get();
    public string UpscaleDone => Get();
    public string UpscaleFailed => Get();
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
    public string SectionAppearance => Get();
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
    public string SectionGeneral => Get();
    public string DefaultBrushLabel => Get();
    public string MaxHistoryLabel => Get();
    public string MaxHistoryHint => Get();
    public string SectionModels => Get();
    public string ModelsDirLabel => Get();
    public string OpenModelsFolder => Get();
    public string OpenFolderFailed => Get();
    public string Close => Get();
}
