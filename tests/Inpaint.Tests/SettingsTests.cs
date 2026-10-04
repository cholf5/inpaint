using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Inpaint.App;
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
                CheckUpdateOnStartup = true,
                InpaintOnStrokeRelease = false, // 默认已开，存非默认方向验证往返
                LastExportFormat = ExportFormat.WebP,
                LastExportQuality = 70,
            };
            SettingsService.Save(settings, path);

            var loaded = SettingsService.Load(path);

            Assert.Equal(AppTheme.Dark, loaded.Theme);
            Assert.Equal(AppLanguage.English, loaded.Language);
            Assert.Equal(AccelerationMode.Cpu, loaded.UpscaleDevice);
            Assert.Equal(88, loaded.DefaultBrushSize);
            Assert.Equal(40, loaded.MaxHistory);
            Assert.True(loaded.CheckUpdateOnStartup);
            Assert.False(loaded.InpaintOnStrokeRelease);
            Assert.Equal(ExportFormat.WebP, loaded.LastExportFormat);
            Assert.Equal(70, loaded.LastExportQuality);
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
        // 启动检查更新默认关：纯本地应用，启动联网必须 opt-in
        Assert.False(missing.CheckUpdateOnStartup);
        // 涂抹松手立即修复默认开：与 Web 版手感一致
        Assert.True(missing.InpaintOnStrokeRelease);
        // 上次导出参数默认 PNG/85（与导出对话框的原默认一致）
        Assert.Equal(ExportFormat.Png, missing.LastExportFormat);
        Assert.Equal(ImageExporter.DefaultQuality, missing.LastExportQuality);

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
        File.WriteAllText(path, """{"Theme":"Dark","MaxHistory":1000,"DefaultBrushSize":9999,"LastExportQuality":0}""");
        try
        {
            var loaded = SettingsService.Load(path);

            Assert.Equal(100, loaded.MaxHistory);
            Assert.Equal(160, loaded.DefaultBrushSize);
            Assert.Equal(1, loaded.LastExportQuality);
        }
        finally
        {
            File.Delete(path);
        }

        // 枚举存名字，但手写成越界数字也会被收敛（JsonStringEnumConverter 接受数字）
        var enumPath = TempPath(".json");
        File.WriteAllText(enumPath, """{"LastExportFormat":7}""");
        try
        {
            Assert.Equal(ExportFormat.WebP, SettingsService.Load(enumPath).LastExportFormat);
        }
        finally
        {
            File.Delete(enumPath);
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

            Assert.Equal("Save…", t.Save);
            Assert.Equal(AppLanguage.English, t.CurrentLanguage);
            // 全属性通知：绑定（含派生的 DropHint）都能刷新
            Assert.Contains(nameof(Translations.Open), raised);
            Assert.Contains(nameof(Translations.DropHint), raised);

            t.SetLanguage(AppLanguage.SimplifiedChinese);

            Assert.Equal("保存…", t.Save);
            Assert.Equal(AppLanguage.SimplifiedChinese, t.CurrentLanguage);
        }
        finally
        {
            t.PropertyChanged -= Handler;
        }
    }

    [Fact]
    public void About_版本号与作者信息()
    {
        // 版本取 Inpaint.App 程序集（非入口程序集，避免测试宿主版本干扰）
        Assert.Matches(@"^v\d+\.\d+\.\d+$", SettingsViewModel.AppVersion);
        // 作者名中英文一致（词典仅中文源，英文回退到中文）
        Assert.Equal("周尔复", Translations.Instance.AuthorName);
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
        vm.CheckUpdateOnStartup = true;
        Assert.True(settings.CheckUpdateOnStartup);
        vm.InpaintOnStrokeRelease = true;
        Assert.True(settings.InpaintOnStrokeRelease);

        // ItemsSource 重建瞬间 ComboBox 可能回写 -1（无选中），应被忽略
        vm.ThemeIndex = -1;
        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [AvaloniaFact]
    public void SettingsViewModel_语言切换后选项文案刷新且选中项保持()
    {
        var settings = new AppSettings { Theme = AppTheme.Dark };
        var vm = new SettingsViewModel(settings);

        Translations.Instance.SetLanguage(AppLanguage.English);

        // 选项实例不变、只换 Label：选区不因 ItemsSource 重建而丢失/清空
        Assert.Equal("Follow system", vm.ThemeOptions[0].Label);
        Assert.Equal("GPU (CoreML)", vm.DeviceOptions[2].Label);
        Assert.Equal(2, vm.ThemeIndex);
    }

    // ---- 检查更新（「关于」页命令，假 handler 不打真实网络）----

    private static SettingsViewModel MakeUpdateVm(string releaseJson, string currentVersion = "v1.0.0") =>
        new(new AppSettings())
        {
            UpdateCheckerFactory = () => new UpdateChecker(
                new FakeHandler(_ => UpdateCheckerTests.JsonResponse(releaseJson)), currentVersion),
        };

    [AvaloniaFact]
    public async Task CheckForUpdate_发现新版本_状态行与跳转链接()
    {
        // 断言中文字符串，先固定语言（进程级单例，其他测试可能留在英文）
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        var vm = MakeUpdateVm(UpdateCheckerTests.LatestReleaseJson);

        await vm.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.Equal("发现新版本 v1.2.3", vm.UpdateCheckStatus);
        Assert.Equal("https://github.com/cholf5/inpaint/releases/tag/v1.2.3", vm.ReleaseUrl);
        // 检查结束恢复按钮可用
        Assert.True(vm.CheckForUpdateCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task CheckForUpdate_已是最新_隐藏跳转链接()
    {
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        var vm = MakeUpdateVm("""{"tag_name":"v1.0.0"}""");

        await vm.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.Equal("已是最新版本（v1.0.0）", vm.UpdateCheckStatus);
        Assert.Null(vm.ReleaseUrl);
    }

    [AvaloniaFact]
    public async Task CheckForUpdate_失败_状态行显示原因不抛异常()
    {
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        var vm = new SettingsViewModel(new AppSettings())
        {
            UpdateCheckerFactory = () => new UpdateChecker(
                new FakeHandler(_ => throw new HttpRequestException("offline")), "v1.0.0"),
        };

        await vm.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.Equal("检查更新失败：offline", vm.UpdateCheckStatus);
        Assert.Null(vm.ReleaseUrl);
    }

    [AvaloniaFact]
    public async Task About页检查更新_真实XAML绑定_跳转按钮随结果显隐()
    {
        // TabControl 只实例化选中页签：先切「关于」再取控件（范例同 LanguageLiveSwitchTests）
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        var window = new SettingsWindow { DataContext = MakeUpdateVm(UpdateCheckerTests.LatestReleaseJson) };
        window.Show();
        try
        {
            var tabs = window.GetVisualDescendants().OfType<TabItem>().ToList();
            var aboutTab = Assert.Single(tabs, t => (t.Header as string) == Translations.Instance.SectionAbout);
            aboutTab.IsSelected = true;
            Dispatcher.UIThread.RunJobs();

            var jumpButton = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => b.Content as string == Translations.Instance.OpenReleasePage);
            Assert.False(jumpButton.IsVisible); // 初始无结果，跳转按钮隐藏
            var checkButton = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => b.Content as string == Translations.Instance.CheckUpdateButton);

            checkButton.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var vm = (SettingsViewModel)window.DataContext!;
            Assert.Equal("发现新版本 v1.2.3", vm.UpdateCheckStatus);
            Assert.True(jumpButton.IsVisible); // 发现新版本后出现跳转按钮
            Assert.Equal(vm.ReleaseUrl, jumpButton.Tag);
        }
        finally
        {
            window.Close();
        }
    }
}
