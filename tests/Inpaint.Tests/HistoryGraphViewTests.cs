using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Inpaint.App;
using Inpaint.App.Controls;
using Inpaint.App.Localization;
using Inpaint.App.Services;
using Inpaint.App.ViewModels;

namespace Inpaint.Tests;

/// <summary>
/// HistoryGraphView 渲染与交互：竖向 git 图（车道列 + 时间行）绘制不抛异常、
/// 分叉车道按配色着色、左键命中预览、右键弹菜单不抛异常，并输出调试快照供人工查看图形态。
/// </summary>
public class HistoryGraphViewTests
{
    public HistoryGraphViewTests() =>
        // 断言里依赖中文字符串（如「已复制」），固定语言避免随系统/用户设置漂移
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);

    // 与 HistoryGraphView 布局常量保持一致
    private const double MarginX = 14, LanePitch = 72, MarginTop = 12, RowHeight = 104, DotOffsetY = 16;

    private static WriteableBitmap MakeBitmap(int width, int height)
    {
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888);
        using var frame = bmp.Lock();
        System.Runtime.InteropServices.Marshal.Copy(
            new byte[frame.RowBytes * height], 0, frame.Address, frame.RowBytes * height);
        return bmp;
    }

    /// <summary>原图 → 修复(2×2) → 放大(3×3)，撤销后从 2×2 再修复(4×4)：
    /// 3×3 延续主干车道，4×4 分叉开新车道，当前节点为 4×4。</summary>
    private static MainWindowViewModel MakeForkedHistory()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        vm.PushHistory(MakeBitmap(2, 2), "修复");
        vm.PushHistory(MakeBitmap(3, 3), "放大 ×4");
        vm.UndoCommand.Execute(null);
        vm.PushHistory(MakeBitmap(4, 4), "修复");
        return vm;
    }

    private static Window ShowGraph(MainWindowViewModel vm, out HistoryGraphView view)
    {
        view = new HistoryGraphView
        {
            Nodes = vm.HistoryNodes,
            CurrentNode = vm.CurrentNode,
            SelectNodeCommand = vm.SelectNodeCommand,
            CopyNodeCommand = vm.CopyNodeCommand,
            SaveNodeCommand = vm.SaveNodeCommand,
        };
        var window = new Window { Width = 320, Height = 520, Content = new ScrollViewer { Content = view } };
        window.Show();
        window.CaptureRenderedFrame();
        return window;
    }

    private static double LaneX(int lane) => MarginX + lane * LanePitch + LanePitch / 2;

    private static double NodeY(int row) => MarginTop + row * RowHeight + DotOffsetY;

    [AvaloniaFact]
    public void 竖向图渲染_分叉接头与当前节点按车道配色()
    {
        var vm = MakeForkedHistory();
        var window = ShowGraph(vm, out _);

        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var pixels = MainWindowViewModel.ExtractBgra(frame);
        int width = frame.PixelSize.Width;

        // 4×4 分叉节点圆点（第 1 车道第 3 行、当前节点）：车道 1 配色（玫红 #B8437A）
        Assert.True(Pixel(pixels, width, (int)LaneX(1), (int)NodeY(3)) is var dot
                && Math.Abs(dot.r - 184) < 14 && Math.Abs(dot.g - 67) < 14 && Math.Abs(dot.b - 122) < 14,
            $"分叉当前节点应为车道 1 玫红圆点，实际 RGB=({dot.r},{dot.g},{dot.b})");

        // 2×2 → 4×4 的分叉水平接头（行 1 高度、两车道之间）：同为玫红
        Assert.True(Pixel(pixels, width, (int)((LaneX(0) + LaneX(1)) / 2), (int)NodeY(1)) is var fork && fork.r > 150,
            $"分叉接头应为玫红，实际 RGB=({fork.r},{fork.g},{fork.b})");

        // 主干车道竖线（行 0 缩略图下方与行 1 缩略图上方之间的空隙）：灰色
        Assert.True(Pixel(pixels, width, (int)LaneX(0), 100) is var line
                && line.r is > 140 and < 215 && Math.Abs(line.b - line.r) < 14 && Math.Abs(line.g - line.r) < 14,
            $"主干车道线应为灰色，实际 RGB=({line.r},{line.g},{line.b})");

        // 调试快照（人工查看图形态）
        using (var fs = File.Create(Path.Combine(Path.GetTempPath(), "inpaint-history-graph.png")))
            frame.Save(fs);

        window.Close();
    }

    [AvaloniaFact]
    public void 左键点击节点_预览并切换当前()
    {
        var vm = MakeForkedHistory();
        var window = ShowGraph(vm, out _);

        // 2×2 节点：车道 0 行 1
        window.MouseDown(new Point(LaneX(0), NodeY(1)), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new PixelSize(2, 2), vm.CurrentImage!.PixelSize);
        Assert.Equal(new PixelSize(2, 2), vm.MaskImage!.PixelSize);
        Assert.True(vm.HistoryNodes[1].IsCurrent);
        Assert.Same(vm.HistoryNodes[1], vm.CurrentNode);

        window.Close();
    }

    [AvaloniaFact]
    public void 右键节点_弹出菜单不抛异常()
    {
        var vm = MakeForkedHistory();
        var window = ShowGraph(vm, out _);

        window.MouseDown(new Point(LaneX(0), NodeY(1)), MouseButton.Right);
        window.MouseUp(new Point(LaneX(0), NodeY(1)), MouseButton.Right, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        window.Close();
        Assert.True(true); // 能走到这里即未抛异常
    }

    [AvaloniaFact]
    public void 主窗口历史Graph_带分叉数据整窗渲染冒烟()
    {
        var window = new MainWindow();
        var viewModel = (MainWindowViewModel)window.DataContext!;
        viewModel.AdoptBitmap(MakeBitmap(4, 3));
        viewModel.PushHistory(MakeBitmap(8, 6), "修复");
        viewModel.PushHistory(MakeBitmap(12, 9), "放大 ×4");
        viewModel.UndoCommand.Execute(null);
        viewModel.PushHistory(MakeBitmap(6, 5), "修复");

        window.Show();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var graph = window.GetVisualDescendants().OfType<HistoryGraphView>().Single();
        Assert.Same(viewModel.CurrentNode, graph.CurrentNode);
        Assert.Equal(4, graph.Nodes!.Count);
        Assert.Equal(2, graph.Nodes.Max(n => n.LaneIndex) + 1);

        // 复制命令直接走 headless 剪贴板整条链路
        viewModel.CopyNodeCommand.Execute(viewModel.HistoryNodes[2]);
        Dispatcher.UIThread.RunJobs();
        Assert.StartsWith("已复制", viewModel.StatusText);

        // 调试快照（人工查看整个历史面板）
        using (var fs = File.Create(Path.Combine(Path.GetTempPath(), "inpaint-history-panel.png")))
            frame.Save(fs);

        window.Close();
    }

    private static (byte b, byte g, byte r, byte a) Pixel(byte[] bgra, int width, int x, int y)
    {
        int index = (y * width + x) * 4;
        return (bgra[index], bgra[index + 1], bgra[index + 2], bgra[index + 3]);
    }
}
