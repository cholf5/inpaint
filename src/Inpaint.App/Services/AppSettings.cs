using CommunityToolkit.Mvvm.ComponentModel;
using Inpaint.Inference;

namespace Inpaint.App.Services;

/// <summary>应用主题：跟随系统、浅色、深色（映射到 Fluent 主题变体）。</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>界面语言：跟随系统（按系统 UI 文化解析，非中文一律回退英文，见 Translations.Resolve）。</summary>
public enum AppLanguage
{
    System,
    SimplifiedChinese,
    English,
}

/// <summary>
/// 用户可调设置。运行期为共享单实例（App 加载后传给 MainWindow/ViewModel/设置窗口），
/// 任何变更即时生效并落盘。JSON 持久化见 SettingsService。
/// </summary>
public partial class AppSettings : ObservableObject
{
    [ObservableProperty] private AppTheme _theme = AppTheme.System;

    [ObservableProperty] private AppLanguage _language = AppLanguage.System;

    /// <summary>Real-ESRGAN 执行设备；MI-GAN 修复引擎固定 CPU，不在此列。</summary>
    [ObservableProperty] private AccelerationMode _upscaleDevice = AccelerationMode.Auto;

    /// <summary>默认画笔直径，初始值与主界面滑块原默认一致；合法区间见 SettingsService.Sanitize。</summary>
    [ObservableProperty] private double _defaultBrushSize = 40;

    /// <summary>生成历史节点数上限（原 const MaxHistory = 25）。</summary>
    [ObservableProperty] private int _maxHistory = 25;

    /// <summary>启动时检查更新（查询 GitHub latest Release）。默认关：应用卖点是纯本地，启动联网必须显式 opt-in。</summary>
    [ObservableProperty] private bool _checkUpdateOnStartup = false;

    /// <summary>涂抹松手后立即执行修复（与 Web 版手感一致，默认开，可关回按钮式）。开启时主界面隐藏修复按钮；
    /// 清除涂抹按钮无状态化，仅在画布残留未处理涂抹时出现。</summary>
    [ObservableProperty] private bool _inpaintOnStrokeRelease = true;

    /// <summary>上次导出格式：导出对话框初始值的持久化记忆（原 _lastExportOptions 只在内存，重启即丢）。
    /// 不存上次保存目录——涉及隐私且换机后路径无效。合法区间见 SettingsService.Sanitize。</summary>
    [ObservableProperty] private ExportFormat _lastExportFormat = ExportFormat.Png;

    /// <summary>上次导出质量（1..100，仅 JPEG/WebP 生效；PNG 无损不显示但仍记忆，切回有损格式时沿用）。</summary>
    [ObservableProperty] private int _lastExportQuality = ImageExporter.DefaultQuality;
}
