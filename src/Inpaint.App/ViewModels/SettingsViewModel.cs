using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Translations _t = Translations.Instance;

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
