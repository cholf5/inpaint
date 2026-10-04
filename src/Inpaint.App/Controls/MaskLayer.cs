using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Inpaint.App.Controls;

/// <summary>
/// 遮罩层 = 全分辨率权威数据 + 低分辨率显示 overlay，两层同步写入：
/// <see cref="Data"/> 每像素 1 字节（255 = 待修复），推理时直接转模型遮罩，
/// 省去 4 字节/像素的位图中转与灰度提纯；<see cref="Overlay"/> 是长边受
/// <see cref="OverlayMaxSide"/> 钳制的 WriteableBitmap，供画布每帧叠加显示。
/// overlay 分辨率必须与原图解耦：WriteableBitmap 涂抹失效后整张重传 GPU（无增量更新），
/// 全分辨率遮罩在超大图上等于每帧上传数百 MB，是涂抹掉帧的根源。
/// </summary>
public sealed class MaskLayer : IDisposable
{
    /// <summary>overlay 长边上限：每帧 GPU 重传 ≤ 2048²×4 ≈ 16MB，与原图分辨率无关。internal 供单测。</summary>
    internal const int OverlayMaxSide = 2048;

    public PixelSize ImageSize { get; }

    /// <summary>权威遮罩数据（行主序，长度 = 宽×高）：255 = 待修复，0 = 保留。</summary>
    public byte[] Data { get; }

    /// <summary>显示 overlay（Bgra8888：白色不透明笔触 + 透明背景），内容与 Data 等价。</summary>
    public WriteableBitmap Overlay { get; }

    private readonly PixelSize _overlaySize;

    public MaskLayer(PixelSize imageSize)
    {
        ImageSize = imageSize;
        Data = new byte[(long)imageSize.Width * imageSize.Height];
        double scale = Math.Min(1.0, (double)OverlayMaxSide / Math.Max(imageSize.Width, imageSize.Height));
        _overlaySize = new PixelSize(
            Math.Max(1, (int)Math.Round(imageSize.Width * scale)),
            Math.Max(1, (int)Math.Round(imageSize.Height * scale)));
        Overlay = new WriteableBitmap(_overlaySize, new Vector(96, 96), PixelFormats.Bgra8888);
        Clear();
    }

    /// <summary>以图片像素坐标把实心圆盘写入权威数据与 overlay，越界部分钳掉。</summary>
    public void PaintDisc(double cx, double cy, double radius)
    {
        if (radius <= 0) return;
        PaintDiscData(cx, cy, radius);
        PaintDiscOverlay(cx, cy, radius);
    }

    /// <summary>清空两层（复用缓冲，避免大图上重复整块分配）。</summary>
    public void Clear()
    {
        Array.Clear(Data);
        using var frame = Overlay.Lock();
        unsafe
        {
            new Span<byte>((void*)frame.Address, frame.RowBytes * _overlaySize.Height).Clear();
        }
    }

    public void Dispose() => Overlay.Dispose();

    private void PaintDiscData(double cx, double cy, double radius)
    {
        int w = ImageSize.Width;
        int x0 = Math.Max(0, (int)Math.Floor(cx - radius));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(cx + radius));
        int y0 = Math.Max(0, (int)Math.Floor(cy - radius));
        int y1 = Math.Min(ImageSize.Height - 1, (int)Math.Ceiling(cy + radius));
        double r2 = radius * radius;
        for (int y = y0; y <= y1; y++)
        {
            double dy = y - cy;
            int row = y * w;
            for (int x = x0; x <= x1; x++)
            {
                double dx = x - cx;
                if (dx * dx + dy * dy <= r2) Data[row + x] = 255;
            }
        }
    }

    private void PaintDiscOverlay(double cx, double cy, double radius)
    {
        // 圆盘按 overlay/image 的轴缩放比映射；缩到不足半个 overlay 像素时钳到 0.5，
        // 保证极端缩小视图下的落笔至少点亮圆心像素（权威数据仍按全分辨率记录）
        double sx = (double)_overlaySize.Width / ImageSize.Width;
        double sy = (double)_overlaySize.Height / ImageSize.Height;
        double ox = cx * sx;
        double oy = cy * sy;
        double orx = Math.Max(radius * sx, 0.5);
        double ory = Math.Max(radius * sy, 0.5);
        int w = _overlaySize.Width;
        int h = _overlaySize.Height;
        int x0 = Math.Max(0, (int)Math.Floor(ox - orx));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(ox + orx));
        int y0 = Math.Max(0, (int)Math.Floor(oy - ory));
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(oy + ory));
        if (x1 < x0 || y1 < y0) return;
        using var frame = Overlay.Lock();
        unsafe
        {
            var basePtr = (byte*)frame.Address;
            int stride = frame.RowBytes;
            for (int y = y0; y <= y1; y++)
            {
                double dy = (y - oy) / ory;
                var row = basePtr + (long)stride * y;
                for (int x = x0; x <= x1; x++)
                {
                    double dx = (x - ox) / orx;
                    if (dx * dx + dy * dy > 1) continue;
                    var p = row + x * 4;
                    p[0] = 255;
                    p[1] = 255;
                    p[2] = 255;
                    p[3] = 255;
                }
            }
        }
    }
}
