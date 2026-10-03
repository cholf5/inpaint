using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Inpaint.App.Localization;
using Inpaint.App.Services;
using Inpaint.Inference;

namespace Inpaint.App.ViewModels;

/// <summary>
/// 设置窗口：修改实时写回共享 AppSettings（主题/语言由 App 订阅生效，设备由主 ViewModel 订阅重建引擎）。
/// 下拉选项用字符串数组 + SelectedIndex 与枚举按下标映射，避免泛型选项类型的绑定模板问题；
/// 语言切换时重建选项标签并重发选中索引保持选中项。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Translations _t = Translations.Instance;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        RebuildOptionLabels();
        Translations.Instance.PropertyChanged += OnLanguageChanged;
    }

    /// <summary>窗口关闭时解除对全局 Translations 的订阅，避免本 VM 被 singleton 事件钉住不释放。</summary>
    public void Detach() => Translations.Instance.PropertyChanged -= OnLanguageChanged;

    public IReadOnlyList<string> ThemeOptions { get; private set; } = [];
    public IReadOnlyList<string> LanguageOptions { get; private set; } = [];
    public IReadOnlyList<string> DeviceOptions { get; private set; } = [];

    /// <summary>AppTheme { System, Light, Dark } 与 ThemeOptions 下标一一对应。</summary>
    public int ThemeIndex
    {
        get => (int)_settings.Theme;
        set
        {
            // ComboBox 在 ItemsSource 重建瞬间可能回写 -1（无选中），忽略负值避免设置被清掉
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

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => RebuildOptionLabels();

    /// <summary>重建选项标签（语言切换后刷新）。先换列表再重发选中索引，负值回写已被 setter 忽略。</summary>
    private void RebuildOptionLabels()
    {
        ThemeOptions = [_t.FollowSystem, _t.ThemeLight, _t.ThemeDark];
        LanguageOptions = [_t.FollowSystem, _t.LangChinese, _t.LangEnglish];
        DeviceOptions = [_t.DeviceAuto, _t.DeviceCpu, _t.DeviceGpu];
        OnPropertyChanged(nameof(ThemeOptions));
        OnPropertyChanged(nameof(LanguageOptions));
        OnPropertyChanged(nameof(DeviceOptions));
        OnPropertyChanged(nameof(ThemeIndex));
        OnPropertyChanged(nameof(LanguageIndex));
        OnPropertyChanged(nameof(DeviceIndex));
    }
}
