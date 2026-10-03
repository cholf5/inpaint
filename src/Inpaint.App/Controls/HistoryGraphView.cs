using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Inpaint.App.Localization;
using Inpaint.App.ViewModels;

namespace Inpaint.App.Controls;

/// <summary>
/// 生成历史 Graph 视图（整图自绘，git 风格竖向图）：
/// 车道为纵向列（0 = 原图主干，分叉按发生顺序向右），行按创建时间从上往下；
/// 每个节点 = 车道上的圆点 + 圆点下方挂缩略图 + 操作名标签，连线穿过圆点串成 beads-on-a-string；
/// 左键点击节点预览（SelectNodeCommand），右键节点弹出 复制/下载 菜单；非当前路径的分支整体压暗。
/// </summary>
public class HistoryGraphView : Control
{
    private const double LanePitch = 72;      // 车道列距
    private const double RowHeight = 104;     // 行距
    private const double MarginX = 14;
    private const double MarginTop = 12;
    private const double DotOffsetY = 16;     // 行内圆点纵向位置
    private const double DotRadius = 5;
    private const double CurrentDotRadius = 7;
    private const double ThumbTop = 26;       // 行内缩略图顶部
    private const double ThumbSize = 48;
    private const double LabelTop = 82;       // 行内标签顶部

    /// <summary>车道配色：主干灰，分支按序取彩色（低饱和，参考 git graph 工具）。</summary>
    private static readonly Color[] LaneColors =
    [
        Color.Parse("#FF9E9E9E"),
        Color.Parse("#FFB8437A"),
        Color.Parse("#FF5B9BD1"),
        Color.Parse("#FFC77B3F"),
        Color.Parse("#FF8E6FC0"),
        Color.Parse("#FF4FA86C"),
        Color.Parse("#FFB8954A"),
    ];

    private static readonly IBrush ThumbBackdrop = new SolidColorBrush(Color.Parse("#FFF4F4F6"));
    private static readonly Color LabelColor = Color.Parse("#FF6B6B6B");
    private static readonly Pen DotRingPen = new(Brushes.White, 1.5);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public static readonly StyledProperty<IReadOnlyList<ImageHistoryNode>?> NodesProperty =
        AvaloniaProperty.Register<HistoryGraphView, IReadOnlyList<ImageHistoryNode>?>(nameof(Nodes));

    public static readonly StyledProperty<ImageHistoryNode?> CurrentNodeProperty =
        AvaloniaProperty.Register<HistoryGraphView, ImageHistoryNode?>(nameof(CurrentNode));

    public static readonly StyledProperty<ICommand?> SelectNodeCommandProperty =
        AvaloniaProperty.Register<HistoryGraphView, ICommand?>(nameof(SelectNodeCommand));

    public static readonly StyledProperty<ICommand?> CopyNodeCommandProperty =
        AvaloniaProperty.Register<HistoryGraphView, ICommand?>(nameof(CopyNodeCommand));

    public static readonly StyledProperty<ICommand?> SaveNodeCommandProperty =
        AvaloniaProperty.Register<HistoryGraphView, ICommand?>(nameof(SaveNodeCommand));

    private readonly ContextMenu _nodeMenu = new();
    private readonly MenuItem _copyItem = new();
    private readonly MenuItem _saveItem = new();

    static HistoryGraphView()
    {
        AffectsMeasure<HistoryGraphView>(NodesProperty);
        AffectsRender<HistoryGraphView>(NodesProperty, CurrentNodeProperty);
    }

    public HistoryGraphView()
    {
        _nodeMenu.Items.Add(_copyItem);
        _nodeMenu.Items.Add(_saveItem);
        ContextMenu = _nodeMenu;
        ContextRequested += OnContextRequested;
    }

    public IReadOnlyList<ImageHistoryNode>? Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public ImageHistoryNode? CurrentNode
    {
        get => GetValue(CurrentNodeProperty);
        set => SetValue(CurrentNodeProperty, value);
    }

    public ICommand? SelectNodeCommand
    {
        get => GetValue(SelectNodeCommandProperty);
        set => SetValue(SelectNodeCommandProperty, value);
    }

    public ICommand? CopyNodeCommand
    {
        get => GetValue(CopyNodeCommandProperty);
        set => SetValue(CopyNodeCommandProperty, value);
    }

    public ICommand? SaveNodeCommand
    {
        get => GetValue(SaveNodeCommandProperty);
        set => SetValue(SaveNodeCommandProperty, value);
    }

    private double LaneX(int lane) => MarginX + lane * LanePitch + LanePitch / 2;

    private double NodeY(ImageHistoryNode node) => MarginTop + node.RowIndex * RowHeight + DotOffsetY;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Nodes is not { Count: > 0 } nodes) return default;
        int lanes = 0;
        foreach (var node in nodes) lanes = Math.Max(lanes, node.LaneIndex + 1);
        return new Size(MarginX * 2 + lanes * LanePitch, MarginTop + nodes.Count * RowHeight + 10);
    }

    public override void Render(DrawingContext context)
    {
        if (Nodes is not { Count: > 0 } nodes) return;

        // 车道纵向连线：从车道起点（根节点圆点或分叉父节点圆点）到最后一个节点
        var laneHasPathNode = new HashSet<int>();
        var laneExtents = new Dictionary<int, (double Top, double Bottom)>();
        foreach (var node in nodes)
        {
            if (node.IsOnCurrentPath) laneHasPathNode.Add(node.LaneIndex);
            double top = node.Parent is { } parent ? NodeY(parent) : NodeY(node);
            double bottom = NodeY(node);
            if (laneExtents.TryGetValue(node.LaneIndex, out var extent))
                laneExtents[node.LaneIndex] = (Math.Min(extent.Top, top), Math.Max(extent.Bottom, bottom));
            else
                laneExtents[node.LaneIndex] = (top, bottom);
        }
        foreach (var (lane, extent) in laneExtents)
        {
            if (extent.Bottom - extent.Top < 0.5) continue;
            context.DrawLine(
                LanePen(lane, laneHasPathNode.Contains(lane) ? 1 : 0.45),
                new Point(LaneX(lane), extent.Top), new Point(LaneX(lane), extent.Bottom));
        }

        // 分叉水平接头：从父节点圆点引出到子节点车道（竖直下落段由车道线覆盖）
        foreach (var node in nodes)
        {
            if (node.Parent is not { } parent || parent.LaneIndex == node.LaneIndex) continue;
            double y = NodeY(parent);
            context.DrawLine(
                LanePen(node.LaneIndex, node.IsOnCurrentPath ? 1 : 0.45),
                new Point(LaneX(parent.LaneIndex), y), new Point(LaneX(node.LaneIndex), y));
        }

        // 节点：缩略图挂线 + 圆点 + 标签
        foreach (var node in nodes)
        {
            double x = LaneX(node.LaneIndex);
            double rowTop = MarginTop + node.RowIndex * RowHeight;
            double dim = node.IsOnCurrentPath ? 1.0 : 0.45;
            var laneColor = LaneColor(node.LaneIndex);

            var thumbRect = new Rect(x - ThumbSize / 2, rowTop + ThumbTop, ThumbSize, ThumbSize);
            if (node.Thumbnail is { } thumb)
            {
                using (context.PushClip(new RoundedRect(thumbRect, 6, 6)))
                    context.DrawImage(thumb, thumbRect);
            }
            else
            {
                context.FillRectangle(ThumbBackdrop, thumbRect, 6);
            }
            context.DrawRectangle(null, new Pen(Solid(Blend(laneColor, dim)), 1.5), thumbRect, 6, 6);

            double radius = node.IsCurrent ? CurrentDotRadius : DotRadius;
            context.DrawEllipse(
                Solid(Blend(laneColor, dim)), DotRingPen, new Point(x, NodeY(node)), radius, radius);

            var text = new FormattedText(
                node.Title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 10.5, Solid(Blend(LabelColor, dim)));
            context.DrawText(text, new Point(x - text.Width / 2, rowTop + LabelTop));
        }
    }

    /// <summary>把节点滚进可视区（生成分叉后新节点在图底部/右侧时跟随视野）。</summary>
    public void ScrollNodeIntoView(ImageHistoryNode node)
    {
        if (this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is not { } scrollViewer) return;
        var rect = new Rect(
            MarginX + node.LaneIndex * LanePitch, MarginTop + node.RowIndex * RowHeight, LanePitch, RowHeight);

        double offsetY = scrollViewer.Offset.Y;
        if (rect.Top - 8 < offsetY) offsetY = Math.Max(0, rect.Top - 8);
        else if (rect.Bottom + 8 > offsetY + scrollViewer.Bounds.Height)
            offsetY = rect.Bottom + 8 - scrollViewer.Bounds.Height;
        offsetY = Math.Clamp(offsetY, 0, Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Bounds.Height));

        double offsetX = scrollViewer.Offset.X;
        if (rect.Left - 8 < offsetX) offsetX = Math.Max(0, rect.Left - 8);
        else if (rect.Right + 8 > offsetX + scrollViewer.Bounds.Width)
            offsetX = rect.Right + 8 - scrollViewer.Bounds.Width;
        offsetX = Math.Clamp(offsetX, 0, Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Bounds.Width));

        scrollViewer.Offset = new Vector(offsetX, offsetY);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed && HitTest(e.GetPosition(this)) is { } node)
        {
            if (SelectNodeCommand is { } command && command.CanExecute(node)) command.Execute(node);
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        Cursor = HitTest(e.GetPosition(this)) is null ? null : HandCursor;
        base.OnPointerMoved(e);
    }

    /// <summary>右键请求：命中节点则把菜单命令指向它，空白处不弹菜单。菜单头即时取当前语言。</summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        Point position = e.TryGetPosition(this, out var pointer)
            ? pointer
            : new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (HitTest(position) is not { } node)
        {
            e.Handled = true;
            return;
        }
        _copyItem.Header = Translations.Instance.CopyImage;
        _saveItem.Header = Translations.Instance.DownloadPng;
        _copyItem.Command = CopyNodeCommand;
        _copyItem.CommandParameter = node;
        _saveItem.Command = SaveNodeCommand;
        _saveItem.CommandParameter = node;
        // 不标记 Handled，让 ContextMenu 在指针位置弹出
    }

    private ImageHistoryNode? HitTest(Point p)
    {
        if (Nodes is not { Count: > 0 }) return null;
        foreach (var node in Nodes)
        {
            var cell = new Rect(
                MarginX + node.LaneIndex * LanePitch, MarginTop + node.RowIndex * RowHeight, LanePitch, RowHeight);
            if (cell.Contains(p)) return node;
        }
        return null;
    }

    private static Color LaneColor(int lane) => LaneColors[lane % LaneColors.Length];

    /// <summary>未在当前路径上的分支压暗：按系数调低透明度。</summary>
    private static Color Blend(Color color, double factor) =>
        Color.FromArgb((byte)(color.A * factor), color.R, color.G, color.B);

    private static IBrush Solid(Color color) => new SolidColorBrush(color);

    private static Pen LanePen(int lane, double dim) =>
        new(Solid(Blend(LaneColor(lane), dim)), 1.6, lineCap: PenLineCap.Round);
}
