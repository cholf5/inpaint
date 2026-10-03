using Avalonia.Media.Imaging;

namespace Inpaint.App.ViewModels;

/// <summary>
/// 生成历史树的一个节点（一次载入 / 修复 / 放大的结果），HistoryGraphView 以 git Graph 方式绘制：
/// 车道为纵向列（时间从上往下），第一子节点延续父节点车道，基于中间节点重新生成时新子节点开右侧新车道，
/// 已有分支所在车道永不改变。
/// 布局字段（LaneIndex/RowIndex）由 ViewModel 重建时统一计算。
/// </summary>
public sealed class ImageHistoryNode
{
    private static int _nextId;

    internal ImageHistoryNode(Bitmap image, string title, ImageHistoryNode? parent)
    {
        Image = image;
        Title = title;
        Parent = parent;
        Id = ++_nextId;
        Detail = $"{image.PixelSize.Width}×{image.PixelSize.Height} · {DateTimeOffset.Now:HH:mm}";
    }

    /// <summary>创建序号，行序与历史超限裁剪都按它来。</summary>
    internal int Id { get; }

    /// <summary>全分辨率位图，由 ViewModel 统一 Dispose。</summary>
    public Bitmap Image { get; }

    /// <summary>节点下挂的缩略图，随节点一起由 ViewModel Dispose。</summary>
    public Bitmap? Thumbnail { get; internal set; }

    public string Title { get; }

    /// <summary>尺寸 + 生成时间，供悬停提示等使用。</summary>
    public string Detail { get; }

    public ImageHistoryNode? Parent { get; internal set; }

    /// <summary>子节点按创建先后排列：第一个延续本节点车道，其余各自开新车道。</summary>
    public List<ImageHistoryNode> Children { get; } = [];

    // ---- Graph 布局（每次重建历史时重算） ----

    /// <summary>所在车道列，0 是原图所在的主干道，分叉按发生顺序依次向右。</summary>
    public int LaneIndex { get; internal set; }

    /// <summary>所在行，全图按创建时间从上往下排列。</summary>
    public int RowIndex { get; internal set; }

    public bool IsCurrent { get; internal set; }

    /// <summary>是否在「原图 → 当前预览节点」的祖先链上（未在路径上的分支整体压暗）。</summary>
    public bool IsOnCurrentPath { get; internal set; }
}
