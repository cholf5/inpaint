using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Inpaint.App.Localization;
using Inpaint.App.Services;
using Inpaint.Inference;

namespace Inpaint.App.ViewModels;

/// <summary>设置下拉框选项：实例跨语言稳定，切换语言只改 Label（选区按对象身份天然保持）。
/// ComboBox 须配 ItemTemplate 绑定 Label，选项文案才能随语言刷新。</summary>
public sealed partial class OptionItem : ObservableObject
{
    public OptionItem(string label) => _label = label;

    [ObservableProperty] private string _label;

    public override string ToString() => Label;
}

/// <summary>
/// 设置窗口：修改实时写回共享 AppSettings（主题/语言由 App 订阅生效，设备由主 ViewModel 订阅重建引擎）。
/// 下拉选项用 OptionItem 数组 + SelectedIndex 与枚举按下标映射；语言切换只更新选项 Label，
/// 不重建 ItemsSource（那会异步清空选区、并把旧选中项经双向绑定推回，吞掉语言切换）。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Translations _t = Translations.Instance;

    /// <summary>程序集版本（「关于」页展示），与检查更新的比较基准同源，见 UpdateChecker.CurrentVersion。</summary>
    public static string AppVersion { get; } = UpdateChecker.CurrentVersion;

    // 快捷键页键帽：键名语言无关，但主修饰键随平台而异（macOS ⌘=Meta，其余平台 Ctrl），
    // 须与 MainWindow 实际绑定一致；仿 AppVersion 以 x:Static 引用，不走 Translations
    /// <summary>主修饰键显示符号。</summary>
    public static string ModifierGlyph => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";
    public static string ZoomWheelKeycap => $"{ModifierGlyph}Wheel";
    public static string ZoomKeysKeycap => $"{ModifierGlyph}0/1";
    public static string OpenKeycap => $"{ModifierGlyph}O";
    public static string PasteKeycap => $"{ModifierGlyph}V";
    public static string ExportKeycap => $"{ModifierGlyph}S";
    public static string UndoKeycap => $"{ModifierGlyph}Z";

    // 选项实例一次创建、跨语言复用，顺序与枚举下标一一对应；标签在构造时按当前语言填充
    private readonly OptionItem[] _themeOptions = [new(""), new(""), new("")];
    private readonly OptionItem[] _languageOptions = [new(""), new(""), new("")];
    private readonly OptionItem[] _deviceOptions = [new(""), new(""), new("")];

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        UpdateOptionLabels();
        Translations.Instance.PropertyChanged += OnLanguageChanged;
    }

    /// <summary>窗口关闭时解除对全局 Translations 的订阅，避免本 VM 被 singleton 事件钉住不释放。</summary>
    public void Detach() => Translations.Instance.PropertyChanged -= OnLanguageChanged;

    public IReadOnlyList<OptionItem> ThemeOptions => _themeOptions;
    public IReadOnlyList<OptionItem> LanguageOptions => _languageOptions;
    public IReadOnlyList<OptionItem> DeviceOptions => _deviceOptions;

    /// <summary>AppTheme { System, Light, Dark } 与 ThemeOptions 下标一一对应。</summary>
    public int ThemeIndex
    {
        get => (int)_settings.Theme;
        set
        {
            // ComboBox 在选区重置瞬间可能回写 -1（无选中），忽略负值避免设置被清掉
            if (value >= 0) _settings.Theme = (AppTheme)value;
        }
    }

    /// <summary>AppLanguage { System, SimplifiedChinese, English } 与 LanguageOptions 下标一一对应。</summary>
    public int LanguageIndex
    {
        get => (int)_settings.Language;
        set
        {
            if (value >= 0) _settings.Language = (AppLanguage)value;
        }
    }

    /// <summary>AccelerationMode { Auto, Cpu, Gpu } 与 DeviceOptions 下标一一对应。</summary>
    public int DeviceIndex
    {
        get => (int)_settings.UpscaleDevice;
        set
        {
            if (value >= 0) _settings.UpscaleDevice = (AccelerationMode)value;
        }
    }

    public double DefaultBrushSize
    {
        get => _settings.DefaultBrushSize;
        set => _settings.DefaultBrushSize = Math.Clamp(value, 4, 160);
    }

    /// <summary>NumericUpDown 的 Value 是 double?，经此代理映射到 int 上限。</summary>
    public double? MaxHistoryValue
    {
        get => _settings.MaxHistory;
        set
        {
            if (value is { } v) _settings.MaxHistory = Math.Clamp((int)Math.Round(v), 5, 100);
        }
    }

    /// <summary>启动时检查更新；只写设置，下次启动生效（App 启动时读取执行）。</summary>
    public bool CheckUpdateOnStartup
    {
        get => _settings.CheckUpdateOnStartup;
        set => _settings.CheckUpdateOnStartup = value;
    }

    /// <summary>涂抹松手后立即修复；实时生效，主窗口经设置订阅同步隐藏修复按钮。</summary>
    public bool InpaintOnStrokeRelease
    {
        get => _settings.InpaintOnStrokeRelease;
        set => _settings.InpaintOnStrokeRelease = value;
    }

    // ---- 检查更新（「关于」页）----

    /// <summary>检查进行中：期间禁用检查按钮。瞬态，不随语言切换刷新。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdateCommand))]
    private bool _isCheckingUpdate;

    /// <summary>检查结果状态行；瞬态文本保持出现时的语言（约定同主窗口 StatusText）。</summary>
    [ObservableProperty] private string? _updateCheckStatus;

    /// <summary>发现新版本时的 Release 页地址；null 时隐藏跳转按钮。</summary>
    [ObservableProperty] private string? _releaseUrl;

    /// <summary>供单测注入假检查器（不打真实网络）；null 走真实检查。</summary>
    internal Func<UpdateChecker>? UpdateCheckerFactory;

    private bool CanCheckUpdate() => !IsCheckingUpdate;

    [RelayCommand(CanExecute = nameof(CanCheckUpdate))]
    private async Task CheckForUpdateAsync()
    {
        IsCheckingUpdate = true;
        try
        {
            UpdateCheckStatus = _t.CheckingUpdate;
            ReleaseUrl = null;
            var result = await (UpdateCheckerFactory?.Invoke() ?? new UpdateChecker()).CheckAsync();
            switch (result.Outcome)
            {
                case UpdateCheckOutcome.UpToDate:
                    UpdateCheckStatus = string.Format(_t.UpToDateStatus, UpdateChecker.CurrentVersion);
                    break;
                case UpdateCheckOutcome.UpdateAvailable:
                    UpdateCheckStatus = string.Format(_t.UpdateAvailableStatus, result.LatestVersion);
                    ReleaseUrl = result.ReleaseUrl;
                    break;
                default:
                    UpdateCheckStatus = string.Format(_t.UpdateCheckFailed, result.Error);
                    break;
            }
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => UpdateOptionLabels();

    /// <summary>语言切换后更新选项文案；选项实例与 ItemsSource 全程不变，选区和索引绑定无扰。</summary>
    private void UpdateOptionLabels()
    {
        _themeOptions[0].Label = _t.FollowSystem;
        _themeOptions[1].Label = _t.ThemeLight;
        _themeOptions[2].Label = _t.ThemeDark;
        _languageOptions[0].Label = _t.FollowSystem;
        _languageOptions[1].Label = _t.LangChinese;
        _languageOptions[2].Label = _t.LangEnglish;
        _deviceOptions[0].Label = _t.DeviceAuto;
        _deviceOptions[1].Label = _t.DeviceCpu;
        _deviceOptions[2].Label = _t.DeviceGpu;
    }
}
