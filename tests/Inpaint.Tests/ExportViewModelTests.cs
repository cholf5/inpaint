using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Inpaint.App.Localization;
using Inpaint.App.Services;
using Inpaint.App.ViewModels;

namespace Inpaint.Tests;

/// <summary>
/// 导出对话框 ViewModel：格式切换与质量滑块触发的预估（防抖/缓存/取消）、确认事件的字节复用。
/// 预估用假执行器（不打真实编码器，真实编码语义在 ImageExporterTests）。
/// </summary>
public class ExportViewModelTests
{
    public ExportViewModelTests() =>
        // 断言里依赖中文字符串（如「预估大小：约」），固定语言避免随系统/用户设置漂移
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);

    private static WriteableBitmap MakeBitmap(int width, int height)
    {
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888);
        using var frame = bmp.Lock();
        Marshal.Copy(new byte[frame.RowBytes * height], 0, frame.Address, frame.RowBytes * height);
        return bmp;
    }

    /// <summary>2×2 位图 + 防抖置零的 VM（未启动预估，由测试控制起点）。</summary>
    private static (ExportViewModel Vm, List<(ExportFormat Format, int Quality)> Calls) MakeVm()
    {
        var vm = new ExportViewModel(MakeBitmap(2, 2))
        {
            EstimateDebounce = TimeSpan.Zero,
        };
        var calls = new List<(ExportFormat, int)>();
        vm.EstimateExecutor = (format, quality, _) =>
        {
            calls.Add((format, quality));
            return Task.FromResult(new byte[(int)quality]);
        };
        vm.StartInitialEstimate();
        return (vm, calls);
    }

    private static async Task FlushAsync(ExportViewModel vm)
    {
        await vm.EstimateTask;
        await Dispatcher.UIThread.InvokeAsync(() => { });
    }

    [AvaloniaFact]
    public async Task 启动_默认PNG立即给出预估()
    {
        var (vm, calls) = MakeVm();

        await FlushAsync(vm);

        Assert.Equal(ExportFormat.Png, vm.Format);
        Assert.False(vm.IsQualityVisible); // PNG 无损，质量区块隐藏
        Assert.False(vm.IsEstimating);
        Assert.StartsWith("预估大小：约", vm.EstimateStatus);
        Assert.Equal(((ExportFormat.Png, 0)), Assert.Single(calls)); // PNG 质量不参与键，键值记 0
        Assert.True(vm.TryGetCachedBytes(ExportFormat.Png, 999, out var cached));
        Assert.Empty(cached); // 替身按 quality 产字节，PNG 键下 quality 为 0
    }

    [AvaloniaFact]
    public async Task 切换格式_质量区块显示并重跑预估()
    {
        var (vm, calls) = MakeVm();
        await FlushAsync(vm);

        vm.FormatIndex = (int)ExportFormat.Jpeg;
        await FlushAsync(vm);

        Assert.True(vm.IsQualityVisible);
        Assert.Equal([(ExportFormat.Png, 0), (ExportFormat.Jpeg, 85)], calls);
        Assert.Contains("85 B", vm.EstimateStatus); // 替身按 quality 产字节
        Assert.True(vm.TryGetCachedBytes(ExportFormat.Jpeg, 85, out _));
    }

    [AvaloniaFact]
    public async Task 质量变化_PNG不重跑_有损格式重跑()
    {
        var (vm, calls) = MakeVm();
        await FlushAsync(vm);

        vm.QualitySlider = 50; // PNG：质量不影响结果，不重跑
        await FlushAsync(vm);
        Assert.Equal(((ExportFormat.Png, 0)), Assert.Single(calls));

        vm.FormatIndex = (int)ExportFormat.WebP;
        await FlushAsync(vm);
        Assert.Equal(2, calls.Count);

        vm.QualitySlider = 60;
        await FlushAsync(vm);
        Assert.Equal(3, calls.Count);
        Assert.True(vm.TryGetCachedBytes(ExportFormat.WebP, 60, out _));
    }

    [AvaloniaFact]
    public async Task 切走再切回_命中缓存秒恢复且在跑预估作废()
    {
        // 缓存是单条目设计（大图每份编码数 MB，不能按质量档囤积）：
        // 收益场景是估算还在跑时切回上一组参数——立刻恢复旧结果并取消在跑任务
        var (vm, calls) = MakeVm();
        await FlushAsync(vm);

        // WebP 的估算挂在未完成的替身上（模拟大图编码中），其余格式即时返回；替身统一记录调用
        var slow = new TaskCompletionSource<byte[]>();
        vm.EstimateExecutor = (format, quality, _) =>
        {
            calls.Add((format, quality));
            return format == ExportFormat.WebP ? slow.Task : Task.FromResult(new byte[quality]);
        };

        vm.FormatIndex = (int)ExportFormat.Jpeg;
        await FlushAsync(vm); // (Jpeg, 85) 完成入缓存
        Assert.Equal([(ExportFormat.Png, 0), (ExportFormat.Jpeg, 85)], calls);

        vm.FormatIndex = (int)ExportFormat.WebP; // 挂起中
        vm.FormatIndex = (int)ExportFormat.Jpeg; // 立刻切回：Jpeg 的旧结果仍在缓存
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.False(vm.IsEstimating); // 无需等待，直接恢复
        Assert.True(vm.TryGetCachedBytes(ExportFormat.Jpeg, 85, out var cached));
        Assert.Equal(85, cached.Length);

        slow.SetResult([]); // 收尾：迟到的 WebP 结果应被代数检查丢弃，不影响状态
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.False(vm.IsEstimating);
        Assert.True(vm.TryGetCachedBytes(ExportFormat.Jpeg, 85, out _));
    }

    [AvaloniaFact]
    public async Task 确认_命中缓存直接复用预估字节()
    {
        var (vm, _) = MakeVm();
        var fakeBytes = new byte[30];
        vm.EstimateExecutor = (_, _, _) => Task.FromResult(fakeBytes);
        vm.FormatIndex = (int)ExportFormat.Jpeg;
        await FlushAsync(vm);

        ExportChoice? received = null;
        vm.ExportConfirmed += choice => received = choice;
        vm.ConfirmCommand.Execute(null);
        await vm.ConfirmCommand.ExecutionTask!;

        Assert.NotNull(received);
        Assert.Same(fakeBytes, received!.Bytes); // 预估即编码：同一份字节落盘，保证预估与实际一致
        Assert.Equal(ExportFormat.Jpeg, received.Format);
        Assert.Equal(85, received.Quality);
    }

    [AvaloniaFact]
    public async Task 确认_预估未完成时取消并现编码()
    {
        var vm = new ExportViewModel(MakeBitmap(2, 2)); // 默认 400ms 防抖：启动后立即确认，预估必然还在 Delay 里
        vm.StartInitialEstimate();

        ExportChoice? received = null;
        vm.ExportConfirmed += choice => received = choice;
        vm.ConfirmCommand.Execute(null);
        await vm.ConfirmCommand.ExecutionTask!;

        Assert.NotNull(received);
        Assert.Equal(ExportFormat.Png, received!.Format);
        // 真实编码器产物：PNG 魔数
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, received.Bytes.Take(4));
    }

    [AvaloniaFact]
    public async Task 切语言_选项标签与预估状态行刷新()
    {
        var (vm, _) = MakeVm();
        await FlushAsync(vm);

        Translations.Instance.SetLanguage(AppLanguage.English);

        Assert.Equal("PNG Image", vm.FormatOptions[0].Label);
        Assert.StartsWith("Estimated size: ~", vm.EstimateStatus);
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);
    }

    // ---- 1:1 对比预览（真实编码字节解码，不用替身）----

    [AvaloniaFact]
    public async Task 预估完成_解码真实字节为预览()
    {
        var vm = new ExportViewModel(MakeBitmap(8, 5)) { EstimateDebounce = TimeSpan.Zero };
        vm.StartInitialEstimate();

        await FlushAsync(vm);

        // 预览 = 预估字节解码回位图：与原图同尺寸但是独立实例（= 落盘内容）
        Assert.NotNull(vm.PreviewResult);
        Assert.Equal(new PixelSize(8, 5), vm.PreviewResult!.PixelSize);
        Assert.NotSame(vm.PreviewOriginal, vm.PreviewResult);
    }

    [AvaloniaFact]
    public async Task 再次预估_预览替换为新解码结果()
    {
        var vm = new ExportViewModel(MakeBitmap(8, 5)) { EstimateDebounce = TimeSpan.Zero };
        vm.StartInitialEstimate();
        await FlushAsync(vm);
        var first = vm.PreviewResult;

        vm.FormatIndex = (int)ExportFormat.Jpeg;
        await FlushAsync(vm);

        Assert.NotSame(first, vm.PreviewResult);
        Assert.NotNull(vm.PreviewResult);
    }

    [AvaloniaFact]
    public async Task Detach_清空预览位图()
    {
        var vm = new ExportViewModel(MakeBitmap(8, 5)) { EstimateDebounce = TimeSpan.Zero };
        vm.StartInitialEstimate();
        await FlushAsync(vm);
        Assert.NotNull(vm.PreviewResult);

        vm.Detach();

        Assert.Null(vm.PreviewResult);
    }
}
