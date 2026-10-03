using Avalonia.Controls;
using Avalonia.Input;
using Inpaint.App.ViewModels;

namespace Inpaint.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainWindowViewModel(StorageProvider);
        DataContext = viewModel;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        if (e.DataTransfer.TryGetFiles() is not { } files) return;
        var path = files
            .Select(file => file.Path.LocalPath)
            .FirstOrDefault(File.Exists);
        if (path is not null)
            await viewModel.LoadFromPathAsync(path);
    }
}
