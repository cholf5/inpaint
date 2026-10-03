using System.Globalization;
using Avalonia.Headless.XUnit;
using Inpaint.App.Localization;
using Inpaint.App.Services;
using Inpaint.App.ViewModels;
using Inpaint.Inference;

namespace Inpaint.Tests;

/// <summary>设置持久化、语言解析、Translations 切换与 SettingsViewModel 映射（纯 .NET，无需 Avalonia 平台）。</summary>
public class SettingsTests
{
    private static string TempPath(string suffix) =>
        Path.Combine(Path.GetTempPath(), $"inpaint-settings-{Guid.NewGuid():N}{suffix}");

    [Fact]
    public void SaveLoad_往返保留所有字段()
    {
        var path = TempPath(".json");
        try
        {
            var settings = new AppSettings
            {
                Theme = AppTheme.Dark,
                Language = AppLanguage.English,
                UpscaleDevice = AccelerationMode.Cpu,
                DefaultBrushSize = 88,
                MaxHistory = 40,
            };
            SettingsService.Save(settings, path);

            var loaded = SettingsService.Load(path);

            Assert.Equal(AppTheme.Dark, loaded.Theme);
            Assert.Equal(AppLanguage.English, loaded.Language);
            Assert.Equal(AccelerationMode.Cpu, loaded.UpscaleDevice);
            Assert.Equal(88, loaded.DefaultBrushSize);
            Assert.Equal(40, loaded.MaxHistory);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_文件缺失或损坏_回退默认值()
    {
        var missing = SettingsService.Load(TempPath(".missing.json"));
        Assert.Equal(AppTheme.System, missing.Theme);
        Assert.Equal(AppLanguage.System, missing.Language);
        Assert.Equal(AccelerationMode.Auto, missing.UpscaleDevice);
        Assert.Equal(25, missing.MaxHistory);

        var corruptPath = TempPath(".json");
        File.WriteAllText(corruptPath, "{ not json");
        try
        {
            var corrupt = SettingsService.Load(corruptPath);
            Assert.Equal(AppTheme.System, corrupt.Theme);
            Assert.Equal(25, corrupt.MaxHistory);
        }
        finally
        {
            File.Delete(corruptPath);
        }
    }

    [Fact]
    public void Load_数值越界_收敛到合法区间()
    {
        var path = TempPath(".json");
        File.WriteAllText(path, """{"Theme":"Dark","MaxHistory":1000,"DefaultBrushSize":9999}""");
        try
        {
            var loaded = SettingsService.Load(path);

            Assert.Equal(100, loaded.MaxHistory);
            Assert.Equal(160, loaded.DefaultBrushSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolve_跟随系统按UI文化解析_非中文回退英文()
    {
        Assert.Equal(AppLanguage.SimplifiedChinese, Translations.Resolve(AppLanguage.System, new CultureInfo("zh-CN")));
        Assert.Equal(AppLanguage.SimplifiedChinese, Translations.Resolve(AppLanguage.System, new CultureInfo("zh")));
        Assert.Equal(AppLanguage.English, Translations.Resolve(AppLanguage.System, new CultureInfo("en-US")));
        Assert.Equal(AppLanguage.English, Translations.Resolve(AppLanguage.System, new CultureInfo("ja-JP")));
        // 显式选择的语言不受系统文化影响
        Assert.Equal(AppLanguage.English, Translations.Resolve(AppLanguage.English, new CultureInfo("zh-CN")));
    }

    [AvaloniaFact]
    public void SetLanguage_切换语言并通知全部属性()
    {
        // AvaloniaFact：App 启动创建的 MainWindow 的 VM 订阅了 Translations，
        // 切语言会触发其绑定更新，必须在 headless UI 线程上执行
        var t = Translations.Instance;
        var raised = new List<string?>();
        void Handler(object? s, System.ComponentModel.PropertyChangedEventArgs e) => raised.Add(e.PropertyName);
        t.PropertyChanged += Handler;
        try
        {
            t.SetLanguage(AppLanguage.English);

            Assert.Equal("Save PNG", t.Save);
            Assert.Equal(AppLanguage.English, t.CurrentLanguage);
            // 全属性通知：绑定（含派生的 DropHint）都能刷新
            Assert.Contains(nameof(Translations.Open), raised);
            Assert.Contains(nameof(Translations.DropHint), raised);

            t.SetLanguage(AppLanguage.SimplifiedChinese);

            Assert.Equal("保存 PNG", t.Save);
            Assert.Equal(AppLanguage.SimplifiedChinese, t.CurrentLanguage);
        }
        finally
        {
            t.PropertyChanged -= Handler;
        }
    }

    [Fact]
    public void SettingsViewModel_索引与枚举双向映射()
    {
        var settings = new AppSettings();
        var vm = new SettingsViewModel(settings);

        Assert.Equal(0, vm.ThemeIndex);
        vm.ThemeIndex = 2;
        Assert.Equal(AppTheme.Dark, settings.Theme);
        vm.DeviceIndex = 1;
        Assert.Equal(AccelerationMode.Cpu, settings.UpscaleDevice);
        vm.LanguageIndex = 1;
        Assert.Equal(AppLanguage.SimplifiedChinese, settings.Language);
        vm.DefaultBrushSize = 120;
        Assert.Equal(120, settings.DefaultBrushSize);
        vm.MaxHistoryValue = 40;
        Assert.Equal(40, settings.MaxHistory);

        // ItemsSource 重建瞬间 ComboBox 可能回写 -1（无选中），应被忽略
        vm.ThemeIndex = -1;
        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [AvaloniaFact]
    public void SettingsViewModel_语言切换后选项标签重建且选中项保持()
    {
        var settings = new AppSettings { Theme = AppTheme.Dark };
        var vm = new SettingsViewModel(settings);

        Translations.Instance.SetLanguage(AppLanguage.English);

        Assert.Equal("Follow system", vm.ThemeOptions[0]);
        Assert.Equal("GPU (CoreML)", vm.DeviceOptions[2]);
        Assert.Equal(2, vm.ThemeIndex); // 选中项不因重建而丢失
    }
}
