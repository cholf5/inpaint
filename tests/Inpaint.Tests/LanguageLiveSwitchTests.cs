using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Inpaint.App;
using Inpaint.App.Localization;
using Inpaint.App.Services;
using Inpaint.App.ViewModels;
using InpaintApp = Inpaint.App.App;

namespace Inpaint.Tests;

/// <summary>
/// 运行期切语言端到端回归：App.WireSettings 真实订阅 + 主/设置双窗口真实 XAML 绑定 + 真实 ComboBox 操作。
/// 曾有 bug：SetLanguage 通知风暴中同步重建 ItemsSource，ComboBox 把滞留旧选中项推回，切换被自己吞没。
/// </summary>
public class LanguageLiveSwitchTests
{
    [AvaloniaFact]
    public void 设置窗口切语言_双窗口即时生效且选择不被回写()
    {
        // Translations 是进程级单例，先固定起始语言（其他测试可能把语言留在英文）
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
        var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
        Assert.IsType<InpaintApp>(Application.Current).WireSettings(settings);

        var mainWindow = new MainWindow(settings);
        var settingsWindow = new SettingsWindow { DataContext = new SettingsViewModel(settings) };
        mainWindow.Show();
        settingsWindow.Show();

        // WireSettings 对任何设置变更都会 Save 到真实用户目录，先快照、最后字节级还原
        var snapshot = File.ReadAllBytes(SettingsService.SettingsPath);

        try
        {
            var boxes = settingsWindow.GetVisualDescendants().OfType<ComboBox>().ToList();
            var themeBox = boxes[0];
            var languageBox = boxes[1];
            var deviceBox = boxes[2];
            Assert.Equal("Inpaint — 图片修复与高清化", mainWindow.Title);
            Assert.Equal(1, languageBox.SelectedIndex); // 起始停在「简体中文」

            // 用户选择 English：走真实 ComboBox → 双向绑定 → VM → 共享设置 → App 订阅 → 全窗口绑定刷新
            languageBox.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();

            // 三个下拉框的选中项都应保持（曾回归：主题/设备框选区被 ItemsSource 重建异步清空成空白）
            Assert.Equal(0, themeBox.SelectedIndex);
            Assert.Equal("Follow system", ((OptionItem)themeBox.SelectedItem!).Label);
            Assert.Equal(2, languageBox.SelectedIndex);
            Assert.Equal("English", ((OptionItem)languageBox.SelectedItem!).Label);
            Assert.Equal(0, deviceBox.SelectedIndex);
            Assert.Equal("Auto (recommended)", ((OptionItem)deviceBox.SelectedItem!).Label);

            Assert.Equal(AppLanguage.English, settings.Language);
            Assert.Equal(AppLanguage.English, Translations.Instance.CurrentLanguage);
            Assert.Equal("Inpaint — Inpainting & Upscaling", mainWindow.Title);
            Assert.Equal("Settings", settingsWindow.Title);

            // 反向切回简体中文，三个下拉框同样保持选中并换回中文文案
            languageBox.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(AppLanguage.SimplifiedChinese, settings.Language);
            Assert.Equal("Inpaint — 图片修复与高清化", mainWindow.Title);
            Assert.Equal(0, themeBox.SelectedIndex);
            Assert.Equal("跟随系统", ((OptionItem)themeBox.SelectedItem!).Label);
            Assert.Equal(1, languageBox.SelectedIndex);
            Assert.Equal("简体中文", ((OptionItem)languageBox.SelectedItem!).Label);
            Assert.Equal(0, deviceBox.SelectedIndex);
            Assert.Equal("自动（推荐）", ((OptionItem)deviceBox.SelectedItem!).Label);
        }
        finally
        {
            // 恢复运行期语言，并把测试期间触发的落盘字节级还原
            Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
            File.WriteAllBytes(SettingsService.SettingsPath, snapshot);
            settingsWindow.Close();
            mainWindow.Close();
        }
    }
}
