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
}
