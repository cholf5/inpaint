using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Inpaint.App.Localization;
using Inpaint.App.Services;
using Inpaint.App.ViewModels;
using Inpaint.Inference;

namespace Inpaint.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as SettingsViewModel)?.Detach();
    }

    /// <summary>共享同一 AppSettings 实例，修改实时写回并由订阅方生效。</summary>
    public SettingsWindow(AppSettings settings) : this()
    {
        DataContext = new SettingsViewModel(settings);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnOpenModelsFolder(object? sender, RoutedEventArgs e)
    {
        // 供网络受限场景手动放入模型文件（AGENTS.md 记载的手动兜底路径）
        try
        {
            var directory = Directory.CreateDirectory(ModelStore.ModelsDir);
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null || !await launcher.LaunchDirectoryInfoAsync(directory))
                FolderStatus.Text = string.Format(Translations.Instance.OpenFolderFailed, ModelStore.ModelsDir);
        }
        catch (Exception ex)
        {
            FolderStatus.Text = string.Format(Translations.Instance.OpenFolderFailed, ex.Message);
        }
    }
}
