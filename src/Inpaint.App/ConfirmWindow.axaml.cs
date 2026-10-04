using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Inpaint.App;

/// <summary>
/// 模态确认框（当前用于超大超分）：MainWindow 经 <c>ShowDialog&lt;bool&gt;</c> 打开，
/// 「继续放大」以 true 关闭；取消按钮、标题栏关闭与 Esc 均以 false 关闭（无结果关闭时 ShowDialog 返回默认值 false）。
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    /// <summary>message：提示正文（调用方经 Translations 组装，随打开时的语言）。测试可无参构造。</summary>
    public ConfirmWindow(string message) : this()
    {
        MessageText.Text = message;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 焦点给「取消」：防的正是手滑连点场景，回车/空格不会顺手确认超大任务
        CancelButton.Focus();
    }

    private void OnContinue(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close(false);
        }
    }
}
