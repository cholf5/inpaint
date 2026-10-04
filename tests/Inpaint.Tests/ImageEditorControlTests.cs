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

    private readonly record struct EditorHost(Window Window, ImageEditorControl Editor, MaskLayer Mask);

    private static EditorHost CreateEditor(double brushSize = 40)
    {
        var source = MakeSolidBitmap(SrcW, SrcH);
        var mask = new MaskLayer(new PixelSize(SrcW, SrcH));
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

    /// <summary>权威遮罩数据（255 = 待修复）在 (x,y) 处是否已涂抹。</summary>
    private static bool IsWhite(MaskLayer mask, int x, int y) =>
        mask.Data[y * mask.ImageSize.Width + x] == 255;

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
    public void 滚轮_触发BrushSizeWheel并带原始增量_画笔不可用时不触发()
    {
        var host = CreateEditor();
        double? delta = null;
        host.Editor.BrushSizeWheel += (_, d) => delta = d;

        host.Window.MouseWheel(new Point(WinW / 2.0, WinH / 2.0), new Vector(0, 1), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, delta);

        // 繁忙期间画笔不可用：不触发（不吞事件也无所谓，画布没有别的滚轮消费者）
        host.Editor.IsPaintEnabled = false;
        delta = null;
        host.Window.MouseWheel(new Point(WinW / 2.0, WinH / 2.0), new Vector(0, -1), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(delta);

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
        var editor = new ImageEditorControl { BrushSize = 40 }; // 无 Source
        var window = new Window { Width = WinW, Height = WinH, Content = editor };
        window.Show();
        window.CaptureRenderedFrame();

        window.MouseDown(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        window.Close();
        Assert.True(true); // 能走到这里即未抛异常
    }

    // ---- 视图缩放/平移 ----
    // 适应窗口初始缩放恒为 5（窗口 320×240、图 64×48）；锚点/钳制数学的确定值都基于此推导

    [Fact]
    public void ZoomPanAt_锚点下的图像点在新倍率下不动()
    {
        var size = new Size(400, 300);
        var bounds = new Size(320, 240);
        var pan = new Point(-40, -30); // zoom 1 时锚点 (100,90) 对应图像点 (140,120)
        var anchor = new Point(100, 90);
        var imgPt = new Point(anchor.X - pan.X, anchor.Y - pan.Y);

        var newPan = ImageEditorControl.ZoomPanAt(pan, 1.0, 2.0, anchor, size, bounds);

        Assert.Equal(anchor.X, newPan.X + imgPt.X * 2, 5);
        Assert.Equal(anchor.Y, newPan.Y + imgPt.Y * 2, 5);
    }

    [Fact]
    public void ClampPan_图小于视口居中_大于视口贴边()
    {
        var centered = ImageEditorControl.ClampPan(new Point(50, 50), 1, new Size(100, 80), new Size(320, 240));
        Assert.Equal(new Point(110, 80), centered);

        var pushedInside = ImageEditorControl.ClampPan(new Point(100, 50), 1, new Size(800, 600), new Size(320, 240));
        Assert.Equal(new Point(0, 0), pushedInside);

        var clamped = ImageEditorControl.ClampPan(new Point(-600, -400), 1, new Size(800, 600), new Size(320, 240));
        Assert.Equal(new Point(-480, -360), clamped);
    }

    [AvaloniaFact]
    public void Ctrl滚轮_以光标为锚点缩放_画笔滚轮不受影响()
    {
        var host = CreateEditor();
        double? brushDelta = null;
        host.Editor.BrushSizeWheel += (_, d) => brushDelta = d;

        // 初始适应缩放 5（500%）；窗口中心锚点 → 缩放后仍应指向图像点 (32,24)
        host.Window.MouseWheel(new Point(WinW / 2.0, WinH / 2.0), new Vector(0, 1), RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(6, host.Editor.Zoom, 5);
        Assert.Null(brushDelta);

        // 锚点下的图像点不动：原位落笔涂的仍是 (32,24)，涂抹半径随缩放换算（40/2/6 ≈ 3.33 图像像素）
        host.Window.MouseDown(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsWhite(host.Mask, 32, 24));
        Assert.True(IsWhite(host.Mask, 35, 24));
        Assert.False(IsWhite(host.Mask, 36, 24));

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 画笔不可用时_Ctrl滚轮仍可缩放()
    {
        var host = CreateEditor();
        host.Editor.IsPaintEnabled = false; // 繁忙模拟：画笔不可用，但缩放仍应可用

        host.Window.MouseWheel(new Point(WinW / 2.0, WinH / 2.0), new Vector(0, 1), RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(6, host.Editor.Zoom, 5);

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 捏合_按平台逐事件增量缩放()
    {
        var host = CreateEditor();

        host.Editor.HandleMagnify(new Point(WinW / 2.0, WinH / 2.0), 1.2);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(6, host.Editor.Zoom, 5);

        host.Editor.HandleMagnify(new Point(WinW / 2.0, WinH / 2.0), 0.8);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4.8, host.Editor.Zoom, 5);

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 空格平移模式_左键拖拽移动画布不落笔()
    {
        var host = CreateEditor();
        int strokes = 0;
        host.Editor.StrokeCommitted += (_, _) => strokes++;

        host.Editor.HandleMagnify(new Point(WinW / 2.0, WinH / 2.0), 1.2); // zoom 6 → pan (-32,-24)，有平移余量
        host.Editor.SetPanMode(true);
        host.Window.MouseDown(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Left);
        host.Window.MouseMove(new Point(WinW / 2.0 - 60, WinH / 2.0 - 40), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        host.Window.MouseUp(new Point(WinW / 2.0 - 60, WinH / 2.0 - 40), MouseButton.Left, RawInputModifiers.None);
        host.Editor.SetPanMode(false);
        Dispatcher.UIThread.RunJobs();

        // 拖拽期间不落笔、不算一笔
        Assert.False(IsWhite(host.Mask, 32, 24));
        Assert.Equal(0, strokes);
        // 平移钳制贴边：(-32,-24)+(-60,-40) → X 钳到 [320-384,0] 的 -64，Y 钳到 -48
        Assert.Equal(new Point(-64, -48), host.Editor.Pan);

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 中键拖拽_平移画布不落笔()
    {
        var host = CreateEditor();

        host.Editor.HandleMagnify(new Point(WinW / 2.0, WinH / 2.0), 1.2); // zoom 6 → pan (-32,-24)
        host.Window.MouseDown(new Point(WinW / 2.0, WinH / 2.0), MouseButton.Middle);
        host.Window.MouseMove(new Point(WinW / 2.0 - 30, WinH / 2.0 - 20), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        host.Window.MouseUp(new Point(WinW / 2.0 - 30, WinH / 2.0 - 20), MouseButton.Middle, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new Point(-62, -44), host.Editor.Pan);
        Assert.False(IsWhite(host.Mask, 32, 24));

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 缩放后落笔_须在图片显示区内()
    {
        var host = CreateEditor();

        host.Editor.HandleMagnify(new Point(WinW / 2.0, WinH / 2.0), 1.2); // contentRect = (-32,-24,384,288)
        host.Window.MouseDown(new Point(10, 10), MouseButton.Left);        // 图像点 (7, 5.67)
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsWhite(host.Mask, 7, 5));
        host.Window.MouseUp(new Point(10, 10), MouseButton.Left, RawInputModifiers.None);

        // 显示区外的留白点击不落笔（若无守卫，坐标钳制会把边缘像素涂白）
        host.Window.MouseDown(new Point(360, 120), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.False(IsWhite(host.Mask, 63, 24));

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 快捷缩放动作_步进_实际大小_适应_标签同步()
    {
        var host = CreateEditor();
        Assert.Equal("500%", host.Editor.ZoomLabel);

        host.Editor.ZoomIn();
        Assert.Equal(6.25, host.Editor.Zoom, 5);
        Assert.Equal("625%", host.Editor.ZoomLabel);

        host.Editor.SetActualSize();
        Assert.Equal(1, host.Editor.Zoom, 5);
        Assert.Equal("100%", host.Editor.ZoomLabel);

        host.Editor.FitToWindow();
        Assert.Equal(5, host.Editor.Zoom, 5);
        Assert.Equal("500%", host.Editor.ZoomLabel);

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 更换不同尺寸源图_自动重新适应窗口()
    {
        var host = CreateEditor();
        host.Editor.HandleMagnify(new Point(WinW / 2.0, WinH / 2.0), 1.2);
        Assert.Equal(6, host.Editor.Zoom, 5);

        host.Editor.Source = MakeSolidBitmap(SrcW / 2, SrcH / 2); // 32×24
        host.Window.CaptureRenderedFrame();
        Assert.Equal(10, host.Editor.Zoom, 5); // min(320/32, 240/24)
        Assert.Equal("1000%", host.Editor.ZoomLabel);

        host.Window.Close();
    }

    [AvaloniaFact]
    public void 缩放越界_钳制到范围()
    {
        var host = CreateEditor();

        host.Editor.HandleMagnify(new Point(WinW / 2.0, WinH / 2.0), 100); // 5×100 → 上限 32
        Assert.Equal(32, host.Editor.Zoom, 5);

        host.Editor.HandleMagnify(new Point(WinW / 2.0, WinH / 2.0), 0.001); // 32×0.001 → 下限 0.05
        Assert.Equal(0.05, host.Editor.Zoom, 5);

        host.Window.Close();
    }
}
