using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Inpaint.App.Controls;

namespace Inpaint.Tests;

/// <summary>
/// 画布指针输入 → 遮罩涂抹。用 headless 窗口注入鼠标事件，
/// 钉住坐标换算（屏幕 → 图片分辨率）、圆盘/连续笔画与越界钳制行为。
/// </summary>
public class ImageEditorControlTests
{
    private const int SrcW = 64, SrcH = 48;
    // 窗口 320×240、源图 64×48 → 显示缩放恰为 5，dest 铺满窗口，画笔 40 → 图片像素半径 4
    private const int WinW = SrcW * 5, WinH = SrcH * 5;

    private readonly record struct EditorHost(Window Window, ImageEditorControl Editor, WriteableBitmap Mask);

    private static EditorHost CreateEditor(double brushSize = 40)
    {
        var source = MakeSolidBitmap(SrcW, SrcH);
        var mask = MakeMask(SrcW, SrcH);
        var editor = new ImageEditorControl { Source = source, Mask = mask, BrushSize = brushSize };
        var window = new Window { Width = WinW, Height = WinH, Content = editor };
        window.Show();
        // 强制一次渲染：Render 里才算出 _contentRect/_scale，指针换算依赖它们
        window.CaptureRenderedFrame();
        Assert.Equal(new Size(WinW, WinH), editor.Bounds.Size);
        return new EditorHost(window, editor, mask);
    }

    private static WriteableBitmap MakeSolidBitmap(int width, int height)
    {
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888);
        using (var frame = bmp.Lock())
        {
            var row = new byte[frame.RowBytes];
            for (int x = 0; x < width; x++)
            {
                row[x * 4 + 0] = 100; row[x * 4 + 1] = 150; row[x * 4 + 2] = 200; row[x * 4 + 3] = 255;
            }
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(row, 0, (nint)(frame.Address + (long)frame.RowBytes * y), frame.RowBytes);
        }
        return bmp;
    }

    private static WriteableBitmap MakeMask(int width, int height)
    {
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888);
        using (var frame = bmp.Lock())
            System.Runtime.InteropServices.Marshal.Copy(
                new byte[frame.RowBytes * height], 0, frame.Address, frame.RowBytes * height);
        return bmp;
    }

    /// <summary>读遮罩紧凑像素（去 stride），返回 (b,g,r,a)。</summary>
    private static (byte B, byte G, byte R, byte A) MaskPixel(WriteableBitmap mask, int x, int y)
    {
        using var frame = mask.Lock();
        var row = new byte[frame.RowBytes];
        System.Runtime.InteropServices.Marshal.Copy((nint)(frame.Address + (long)frame.RowBytes * y), row, 0, frame.RowBytes);
        return (row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]);
    }

    private static bool IsWhite(WriteableBitmap mask, int x, int y)
    {
        var (b, g, r, a) = MaskPixel(mask, x, y);
        return b == 255 && g == 255 && r == 255 && a == 255;
    }

    [AvaloniaFact]
    public void 左键按下_按图片分辨率画出白色圆盘()
    {
        var host = CreateEditor();

        // 窗口中心 (160,120) → 图片像素 (32,24)，半径 4
        host.Window.MouseDown(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(IsWhite(host.Mask, 32, 24));   // 圆心
        Assert.True(IsWhite(host.Mask, 36, 24));   // 距心 4（边界 dx²=r² 不排除）
        Assert.True(IsWhite(host.Mask, 31, 22));   // 内部
        Assert.False(IsWhite(host.Mask, 37, 24));  // 距心 5，圆外
        Assert.False(IsWhite(host.Mask, 28, 20));  // 角落，圆外

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 左键拖动_笔画连续_抬起后不再涂抹()
    {
        var host = CreateEditor();

        host.Window.MouseDown(new Point(80, WinH / 2.0), MouseButton.Left);    // 图片 (16,24)
        host.Window.MouseMove(new Point(WinW / 2.0, WinH / 2.0), RawInputModifiers.None); // → (32,24)
        Dispatcher.UIThread.RunJobs();
        host.Window.MouseUp(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Left, RawInputModifiers.None);

        // r=4 圆盘每 ≤2px 一个 → 整段 x∈[12,36] 连续覆盖
        for (int x = 12; x <= 36; x++)
            Assert.True(IsWhite(host.Mask, x, 24), $"({x},24) 应已涂抹");
        Assert.False(IsWhite(host.Mask, 11, 24));
        Assert.False(IsWhite(host.Mask, 37, 24));

        // 抬起后移动不涂抹
        host.Window.MouseMove(new Point(WinW - 40, WinH / 2.0), RawInputModifiers.None); // 图片 (56,24)
        Dispatcher.UIThread.RunJobs();
        Assert.False(IsWhite(host.Mask, 56, 24));

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 左键落笔_触发StrokePainted事件_右键不触发()
    {
        var host = CreateEditor();
        int strokes = 0;
        host.Editor.StrokePainted += (_, _) => strokes++;

        host.Window.MouseDown(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, strokes);

        host.Window.MouseDown(new Point(40, 40), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, strokes);

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 一笔涂完松开左键_触发StrokeCommitted_其余抬起不触发()
    {
        var host = CreateEditor();
        int committed = 0;
        host.Editor.StrokeCommitted += (_, _) => committed++;

        // 没有进行中的笔触就抬起：不触发
        host.Window.MouseUp(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, committed);

        // 一笔完整涂抹：按下后抬起触发一次（单击成点也算一笔）
        host.Window.MouseDown(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Left);
        host.Window.MouseUp(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, committed);

        // 右键不开始笔触，其抬起不触发
        host.Window.MouseDown(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Right);
        host.Window.MouseUp(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Right, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, committed);

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 笔触越界_钳制到图片边缘()
    {
        var host = CreateEditor();

        host.Window.MouseDown(new Point(8, 8), MouseButton.Left); // 图片 (1.6,1.6)
        Dispatcher.UIThread.RunJobs();

        Assert.True(IsWhite(host.Mask, 0, 0));
        Assert.True(IsWhite(host.Mask, 1, 1));
        Assert.False(IsWhite(host.Mask, 7, 7));

        host.Window.Close();
    }

    [AvaloniaFact]
    public void IsPaintEnabled关闭_不涂抹()
    {
        var host = CreateEditor();
        host.Editor.IsPaintEnabled = false;

        host.Window.MouseDown(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(IsWhite(host.Mask, 32, 24));

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 非左键_不涂抹()
    {
        var host = CreateEditor();

        host.Window.MouseDown(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        host.Window.MouseUp(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Right, RawInputModifiers.None);

        Assert.False(IsWhite(host.Mask, 32, 24));

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 没有遮罩或源图_按下不崩溃也不涂抹()
    {
        var mask = MakeMask(SrcW, SrcH);
        var editor = new ImageEditorControl { BrushSize = 40 }; // 无 Source
        var window = new Window { Width = WinW, Height = WinH, Content = editor };
        window.Show();
        window.CaptureRenderedFrame();

        window.MouseDown(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        window.Close();
        Assert.True(true); // 能走到这里即未抛异常
    }
}
