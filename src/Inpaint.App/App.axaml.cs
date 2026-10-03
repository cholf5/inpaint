using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Inpaint.App.Localization;
using Inpaint.App.Services;

namespace Inpaint.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 设置加载一次作为运行期单实例：主题/语言在此应用，后续变更（设置窗口）经 PropertyChanged 生效并落盘
        var settings = SettingsService.Load();
        ApplyTheme(settings.Theme);
        Translations.Instance.SetLanguage(settings.Language);
        WireSettings(settings);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(settings);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 订阅共享设置的变更：主题/语言即时应用，任何变更落盘。internal 供单测复用真实链路
    /// （headless 会话没有桌面生命周期，走不到 OnFrameworkInitializationCompleted）。
    /// </summary>
    internal void WireSettings(AppSettings settings)
    {
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.Theme)) ApplyTheme(settings.Theme);
            else if (e.PropertyName == nameof(AppSettings.Language))
                Translations.Instance.SetLanguage(settings.Language);
            try
            {
                SettingsService.Save(settings);
            }
            catch
            {
                // 设置写入失败不影响运行（磁盘只读等），下次变更会再尝试
            }
        };
    }

    /// <summary>设置主题映射到 Fluent 主题变体：Default 即跟随系统。</summary>
    private static void ApplyTheme(AppTheme theme) =>
        Application.Current!.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
}
