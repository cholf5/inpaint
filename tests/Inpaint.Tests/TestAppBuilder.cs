using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;
using InpaintApp = Inpaint.App.App;
using Xunit.Sdk;

[assembly: AvaloniaTestApplication(typeof(Inpaint.Tests.TestAppBuilder))]

// Translations 是进程级单例，测试间会切换语言；关闭集合并行避免串扰
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Inpaint.Tests;

/// <summary>headless 测试共用入口：复用真实 App（含 Fluent 主题），平台替换为 headless。</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<InpaintApp>()
            // UseHeadlessDrawing=false 走真实 Skia 渲染/位图路径，与生产行为一致（WriteableBitmap.Lock/CopyPixels 才有正确语义）
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}
