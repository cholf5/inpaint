using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Inpaint.App.Localization;
using Inpaint.App.Services;
using Inpaint.App.ViewModels;
using Inpaint.Inference;

namespace Inpaint.Tests;

/// <summary>
/// ViewModel 位图生命周期、命令可用性与生成历史树（git 式分叉）语义。
/// Bitmap 相关操作需要 Avalonia 平台，走 headless。
/// </summary>
public class MainWindowViewModelTests
{
    public MainWindowViewModelTests() =>
        // 断言里依赖中文字符串（如「无法打开图片」），固定语言避免随系统/用户设置漂移
        Translations.Instance.SetLanguage(AppLanguage.SimplifiedChinese);

    private static WriteableBitmap MakeBitmap(int width, int height)
    {
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888);
        using var frame = bmp.Lock();
        Marshal.Copy(new byte[frame.RowBytes * height], 0, frame.Address, frame.RowBytes * height);
        return bmp;
    }

    private static void PushChain(MainWindowViewModel vm, int from, int to)
    {
        for (int i = from; i <= to; i++)
            vm.PushHistory(MakeBitmap(i, i), $"测试{i}");
    }

    [AvaloniaFact]
    public void AdoptBitmap_初始化图片遮罩与命令可用性()
    {
        var vm = new MainWindowViewModel(null, null);
        Assert.False(vm.HasImage);
        Assert.False(vm.InpaintCommand.CanExecute(null));

        vm.AdoptBitmap(MakeBitmap(6, 4));

        Assert.True(vm.HasImage);
        Assert.Equal(new PixelSize(6, 4), vm.CurrentImage!.PixelSize);
        Assert.Equal(new PixelSize(6, 4), vm.MaskLayer!.ImageSize);
        // 回归：HasImage 变化必须通知命令，否则按钮在加载后仍不可用
        Assert.True(vm.InpaintCommand.CanExecute(null));
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.UpscaleCommand.CanExecute(null));
        Assert.True(vm.ResetCommand.CanExecute(null));
        Assert.True(vm.ClearMaskCommand.CanExecute(null));
        Assert.False(vm.UndoCommand.CanExecute(null));
        // 生成历史：第一项永远是原图
        var root = Assert.Single(vm.HistoryNodes);
        Assert.Equal("原图", root.Title);
        Assert.Null(root.Parent);
        Assert.Same(root, vm.CurrentNode);
        Assert.True(root.IsCurrent);
        Assert.NotNull(root.Thumbnail);
        // 测试环境没有剪贴板，复制命令应禁用
        Assert.False(vm.CopyNodeCommand.CanExecute(root));
        // 遮罩权威数据初始全 0（= 保留；经 MaskGrayToChw 全部映射为 255）
        Assert.All(vm.MaskLayer.Data, b => Assert.Equal(0, b));
    }

    [AvaloniaFact]
    public void AdoptBitmap_加载提示通知StatusDisplay刷新()
    {
        var vm = new MainWindowViewModel(null, null);
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.AdoptBitmap(MakeBitmap(6, 4));

        // 回归：状态栏绑定派生属性 StatusDisplay，StatusText 变更必须连带通知，
        // 否则「已加载 W×H…」提示要等下次语言切换才浮现
        Assert.Contains(nameof(MainWindowViewModel.StatusDisplay), notified);
        Assert.StartsWith("已加载 6×4", vm.StatusDisplay);
    }

    [AvaloniaFact]
    public async Task LoadFromStreamAsync_无效流写入StatusText不抛异常()
    {
        var vm = new MainWindowViewModel(null, null);

        await vm.LoadFromStreamAsync(new MemoryStream([1, 2, 3]));

        Assert.StartsWith("无法打开图片", vm.StatusText);
        Assert.False(vm.HasImage);
        Assert.False(vm.InpaintCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void PushHistory_线性生成与撤销()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        PushChain(vm, 2, 3);

        Assert.Equal(new PixelSize(3, 3), vm.CurrentImage!.PixelSize);
        Assert.Equal(3, vm.HistoryNodes.Count);
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.Equal(new PixelSize(2, 2), vm.CurrentImage!.PixelSize);
        vm.UndoCommand.Execute(null);
        Assert.Equal(new PixelSize(1, 1), vm.CurrentImage!.PixelSize);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void PushHistory_撤销后生成产生分叉_旧分支保留()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        PushChain(vm, 2, 3);

        vm.UndoCommand.Execute(null);
        Assert.Equal(new PixelSize(2, 2), vm.CurrentImage!.PixelSize);

        // 基于 2×2 重新生成：git 式分叉，旧分支 3×3 保留不动
        vm.PushHistory(MakeBitmap(4, 4), "修复");

        Assert.Equal(new PixelSize(4, 4), vm.CurrentImage!.PixelSize);
        Assert.Equal(4, vm.HistoryNodes.Count);
        var forkBase = vm.HistoryNodes[1];
        Assert.Equal(new PixelSize(2, 2), forkBase.Image.PixelSize);
        Assert.Equal(2, forkBase.Children.Count);
        // 列表按创建时间排列：3×3 在 4×4 之前
        Assert.Equal(new PixelSize(3, 3), vm.HistoryNodes[2].Image.PixelSize);
        Assert.Equal(new PixelSize(4, 4), vm.HistoryNodes[3].Image.PixelSize);
        // 撤销沿当前链走：4×4 → 2×2 → 原图
        vm.UndoCommand.Execute(null);
        Assert.Equal(new PixelSize(2, 2), vm.CurrentImage!.PixelSize);
        vm.UndoCommand.Execute(null);
        Assert.Equal(new PixelSize(1, 1), vm.CurrentImage!.PixelSize);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void PushHistory_分叉图布局_车道与行分配()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        PushChain(vm, 2, 3);
        vm.UndoCommand.Execute(null);
        vm.PushHistory(MakeBitmap(4, 4), "修复");

        var rows = vm.HistoryNodes;
        // 行 = 创建时间序
        Assert.Equal(0, rows[0].RowIndex);
        Assert.Equal(1, rows[1].RowIndex);
        Assert.Equal(2, rows[2].RowIndex);
        Assert.Equal(3, rows[3].RowIndex);
        // 车道 = 第一子节点延续父车道（git 式原车道不动），分叉子节点开右侧新车道
        Assert.Equal(0, rows[0].LaneIndex);
        Assert.Equal(0, rows[1].LaneIndex);
        Assert.Equal(0, rows[2].LaneIndex);
        Assert.Equal(1, rows[3].LaneIndex);
        // 当前路径 = 原图 → 2×2 → 4×4；3×3 是被搁置的分支
        Assert.True(rows[0].IsOnCurrentPath);
        Assert.True(rows[1].IsOnCurrentPath);
        Assert.False(rows[2].IsOnCurrentPath);
        Assert.False(rows[2].IsCurrent);
        Assert.True(rows[3].IsCurrent);
    }

    [AvaloniaFact]
    public void PushHistory_超限裁剪_优先丢弃最旧的非当前分支()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        PushChain(vm, 2, 24);
        vm.ResetCommand.Execute(null);
        vm.PushHistory(MakeBitmap(100, 100), "修复");
        vm.PushHistory(MakeBitmap(200, 200), "修复");

        // 共 26 项 → 裁掉最旧的非当前分支节点 2×2
        Assert.Equal(25, vm.HistoryNodes.Count);
        // 原图永远是第一项且保留
        Assert.Equal(new PixelSize(1, 1), vm.HistoryNodes[0].Image.PixelSize);
        Assert.Null(vm.HistoryNodes[0].Parent);
        Assert.DoesNotContain(vm.HistoryNodes, n => n.Image.PixelSize == new PixelSize(2, 2));
        // 2×2 之后的链条被拼接到根下，保持完整
        var n3 = vm.HistoryNodes.First(n => n.Image.PixelSize == new PixelSize(3, 3));
        Assert.Same(vm.HistoryNodes[0], n3.Parent);
        // 当前分支不受影响，可一路撤销回原图
        Assert.Equal(new PixelSize(200, 200), vm.CurrentImage!.PixelSize);
        vm.UndoCommand.Execute(null);
        Assert.Equal(new PixelSize(100, 100), vm.CurrentImage!.PixelSize);
        vm.UndoCommand.Execute(null);
        Assert.Equal(new PixelSize(1, 1), vm.CurrentImage!.PixelSize);
    }

    [AvaloniaFact]
    public void PushHistory_纯直链超限_拼接最旧节点且根保留()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        PushChain(vm, 2, 31);

        Assert.Equal(25, vm.HistoryNodes.Count);
        Assert.Equal(new PixelSize(1, 1), vm.HistoryNodes[0].Image.PixelSize);
        Assert.Equal(new PixelSize(31, 31), vm.CurrentImage!.PixelSize);
        // 撤销链仍完整：24 次撤销回到原图
        for (int i = 0; i < 24; i++)
        {
            Assert.True(vm.UndoCommand.CanExecute(null), $"第 {i + 1} 次撤销应可用");
            vm.UndoCommand.Execute(null);
        }
        Assert.Equal(new PixelSize(1, 1), vm.CurrentImage!.PixelSize);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Reset_回到原图且历史保留()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        PushChain(vm, 2, 3);

        vm.ResetCommand.Execute(null);

        Assert.Equal(new PixelSize(1, 1), vm.CurrentImage!.PixelSize);
        // 新语义：历史树保留，可点击图中旧节点切回；根（原图）没有父节点，撤销禁用
        Assert.Equal(3, vm.HistoryNodes.Count);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void SelectNode_预览历史项并作为后续生成基准()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        PushChain(vm, 2, 3);

        vm.SelectNodeCommand.Execute(vm.HistoryNodes[1]);

        Assert.Equal(new PixelSize(2, 2), vm.CurrentImage!.PixelSize);
        Assert.Equal(new PixelSize(2, 2), vm.MaskLayer!.ImageSize);
        Assert.True(vm.HistoryNodes[1].IsCurrent);
        Assert.False(vm.HistoryNodes[2].IsCurrent);
        Assert.True(vm.UndoCommand.CanExecute(null));
        // 在该节点上继续生成 → git 式分叉：新图开新车道，旧分支不动
        vm.PushHistory(MakeBitmap(4, 4), "修复");
        Assert.Equal(new PixelSize(4, 4), vm.CurrentImage!.PixelSize);
        Assert.Equal(2, vm.HistoryNodes[1].Children.Count);
        Assert.Equal(1, vm.HistoryNodes[3].LaneIndex);
    }

    [AvaloniaFact]
    public void AdoptBitmap_重新载入时释放旧历史()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        PushChain(vm, 2, 3);

        vm.AdoptBitmap(MakeBitmap(5, 5));

        var root = Assert.Single(vm.HistoryNodes);
        Assert.Equal(new PixelSize(5, 5), root.Image.PixelSize);
        Assert.Equal(new PixelSize(5, 5), vm.CurrentImage!.PixelSize);
        Assert.Equal(new PixelSize(5, 5), vm.MaskLayer!.ImageSize);
    }

    [AvaloniaFact]
    public void 画笔快捷键命令_步进调整并钳制到滑块范围()
    {
        var vm = new MainWindowViewModel(null, null);
        Assert.Equal(40, vm.BrushSize);

        vm.DecreaseBrushSizeCommand.Execute(null);
        Assert.Equal(39, vm.BrushSize);
        vm.IncreaseBrushSizeCommand.Execute(null);
        Assert.Equal(40, vm.BrushSize);

        for (int i = 0; i < 200; i++) vm.IncreaseBrushSizeCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.MaxBrushSize, vm.BrushSize);
        for (int i = 0; i < 200; i++) vm.DecreaseBrushSizeCommand.Execute(null);
        Assert.Equal(MainWindowViewModel.MinBrushSize, vm.BrushSize);
    }

    [AvaloniaFact]
    public void 画布滚轮_按滚轮步进调整画笔并钳制()
    {
        var vm = new MainWindowViewModel(null, null);
        Assert.Equal(40, vm.BrushSize);

        vm.AdjustBrushSize(MainWindowViewModel.BrushWheelStep);
        Assert.Equal(44, vm.BrushSize);
        vm.AdjustBrushSize(-MainWindowViewModel.BrushWheelStep);
        Assert.Equal(40, vm.BrushSize);

        // 触底钳制到滑块范围
        vm.AdjustBrushSize(1000 * MainWindowViewModel.BrushWheelStep);
        Assert.Equal(MainWindowViewModel.MaxBrushSize, vm.BrushSize);
        vm.AdjustBrushSize(-1000 * MainWindowViewModel.BrushWheelStep);
        Assert.Equal(MainWindowViewModel.MinBrushSize, vm.BrushSize);
    }

    [AvaloniaFact]
    public void Enter修复快捷键_涂抹后才可用_遮罩重建后回到不可用()
    {
        var vm = new MainWindowViewModel(null, null);
        // 无图时不可用
        Assert.False(vm.InpaintByEnterCommand.CanExecute(null));

        vm.AdoptBitmap(MakeBitmap(6, 4));
        // 有图未涂抹：Enter 不可用；修复按钮维持原可用条件，不受涂抹门槛影响（回归）
        Assert.False(vm.InpaintByEnterCommand.CanExecute(null));
        Assert.True(vm.InpaintCommand.CanExecute(null));

        vm.MarkMaskPainted();
        Assert.True(vm.InpaintByEnterCommand.CanExecute(null));

        // 清除涂抹等一切 SetMask 重建遮罩的路径都会复位涂抹状态
        vm.ClearMaskCommand.Execute(null);
        Assert.False(vm.InpaintByEnterCommand.CanExecute(null));
        Assert.False(vm.HasMaskStrokes);

        // 重新涂抹后再次可用；切换历史节点（重建遮罩）同样复位
        vm.MarkMaskPainted();
        Assert.True(vm.InpaintByEnterCommand.CanExecute(null));
        vm.PushHistory(MakeBitmap(8, 8), "测试");
        vm.MarkMaskPainted();
        Assert.True(vm.InpaintByEnterCommand.CanExecute(null));
        vm.SelectNodeCommand.Execute(vm.HistoryNodes[0]);
        Assert.False(vm.InpaintByEnterCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void CreateBitmap_ExtractBgra_往返一致()
    {
        var bgra = new byte[4 * 3 * 2];
        for (int i = 0; i < bgra.Length; i++)
            bgra[i] = (byte)(i * 37 % 256);

        var bitmap = MainWindowViewModel.CreateBitmap(new PixelSize(3, 2), bgra);

        Assert.Equal(bgra, MainWindowViewModel.ExtractBgra(bitmap));
    }

    [AvaloniaFact]
    public void ExtractBgra_Rgba8888源交换红蓝()
    {
        var rgba = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormats.Rgba8888);
        using (var frame = rgba.Lock())
            Marshal.Copy(new byte[] { 1, 2, 3, 255 }, 0, frame.Address, 4);

        // Rgba8888 → Bgra8888：R/B 对调，G/A 不变
        Assert.Equal(new byte[] { 3, 2, 1, 255 }, MainWindowViewModel.ExtractBgra(rgba));
    }

    [AvaloniaFact]
    public void 设置_默认画笔与历史上限生效_设备切换空闲时无副作用()
    {
        var settings = new AppSettings { DefaultBrushSize = 88, MaxHistory = 10 };
        var vm = new MainWindowViewModel(null, null, settings);

        Assert.Equal(88, vm.BrushSize);

        // 历史上限来自设置：推 15 项应裁剪到 10（原 const 25 的行为由默认设置覆盖）
        PushChain(vm, 1, 15);
        Assert.Equal(10, vm.HistoryNodes.Count);

        // 设备切换：引擎尚未创建时应无副作用、不进入忙碌
        settings.UpscaleDevice = AccelerationMode.Gpu;
        Assert.False(vm.IsBusy);

        // 默认画笔变化实时同步到当前画笔
        settings.DefaultBrushSize = 120;
        Assert.Equal(120, vm.BrushSize);
    }

    [AvaloniaFact]
    public void 设置_画笔默认值越界时钳制到滑块范围()
    {
        var settings = new AppSettings { DefaultBrushSize = 9999 };
        var vm = new MainWindowViewModel(null, null, settings);

        Assert.Equal(MainWindowViewModel.MaxBrushSize, vm.BrushSize);
    }

    // ---- 松手即修复（OnStrokeCommitted 经 AutoInpaintExecutor 替身断言，不跑真实推理）----

    [AvaloniaFact]
    public void OnStrokeCommitted_选项关闭_不触发修复()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.Settings.InpaintOnStrokeRelease = false; // 默认开，显式关闭验证按钮式路径
        vm.AdoptBitmap(MakeBitmap(6, 4));
        vm.MarkMaskPainted();
        int triggered = 0;
        vm.AutoInpaintExecutor = () => triggered++;

        vm.OnStrokeCommitted();

        Assert.Equal(0, triggered);
        Assert.True(vm.ShowInpaintButton);
    }

    [AvaloniaFact]
    public void OnStrokeCommitted_选项开启且已涂抹_触发修复()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(6, 4));
        vm.MarkMaskPainted();
        vm.Settings.InpaintOnStrokeRelease = true;
        int triggered = 0;
        vm.AutoInpaintExecutor = () => triggered++;

        vm.OnStrokeCommitted();

        Assert.Equal(1, triggered);
    }

    [AvaloniaFact]
    public void OnStrokeCommitted_选项开启但未涂抹或繁忙_不触发()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(6, 4));
        vm.Settings.InpaintOnStrokeRelease = true;
        int triggered = 0;
        vm.AutoInpaintExecutor = () => triggered++;

        // 未涂抹（新遮罩）：门槛与 Enter 快捷键一致
        vm.OnStrokeCommitted();
        Assert.Equal(0, triggered);

        // 推理进行中：画布已禁涂，理论上收不到松手，仍需兜底不触发
        vm.MarkMaskPainted();
        vm.IsBusy = true;
        vm.OnStrokeCommitted();
        Assert.Equal(0, triggered);
    }

    [AvaloniaFact]
    public void ShowInpaintButton_随松手即修复设置翻转并通知()
    {
        var vm = new MainWindowViewModel(null, null);
        Assert.False(vm.ShowInpaintButton); // 默认开：修复按钮隐藏
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Settings.InpaintOnStrokeRelease = false;

        Assert.True(vm.ShowInpaintButton);
        Assert.Contains(nameof(MainWindowViewModel.ShowInpaintButton), raised);
    }

    [AvaloniaFact]
    public void ShowClearMask_仅画布残留涂抹且非繁忙时显示()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(6, 4));
        Assert.False(vm.ShowClearMask); // 无涂抹

        vm.MarkMaskPainted();
        Assert.True(vm.ShowClearMask);

        // 繁忙期间遮罩属「处理中」，按钮隐藏
        vm.IsBusy = true;
        Assert.False(vm.ShowClearMask);
        vm.IsBusy = false;
        Assert.True(vm.ShowClearMask);

        // 清除（或修复成功）后遮罩清空，按钮消失
        vm.ClearMaskCommand.Execute(null);
        Assert.False(vm.ShowClearMask);
    }

    [AvaloniaFact]
    public void AdoptBitmap_加载提示语随松手即修复设置切换()
    {
        var vm = new MainWindowViewModel(null, null); // 默认开
        vm.AdoptBitmap(MakeBitmap(6, 4));
        Assert.Equal("已加载 6×4，涂抹后松手即自动修复", vm.StatusText);

        vm.Settings.InpaintOnStrokeRelease = false;
        vm.AdoptBitmap(MakeBitmap(6, 4));
        Assert.Equal(
            string.Format(Translations.Instance.LoadedStatus, 6, 4, Translations.Instance.Inpaint),
            vm.StatusText);
    }

    [AvaloniaFact]
    public void SuggestFileName_原图用源文件名_生成节点追加历史标题()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1), "photo.jpg");
        var root = vm.CurrentNode!;
        Assert.Equal("photo", vm.SuggestFileName(root));

        // 生成节点：源文件名 + 历史标题，标题中的空白与非法字符被清理
        vm.PushHistory(MakeBitmap(2, 2), Translations.Instance.NodeInpaint);
        Assert.Equal("photo_修复", vm.SuggestFileName(vm.CurrentNode!));

        vm.PushHistory(MakeBitmap(8, 8), Translations.Instance.NodeUpscale);
        Assert.Equal("photo_放大×4", vm.SuggestFileName(vm.CurrentNode!));

        // 逐级撤销回原图（修复 → 原图）后保存，仍是干净的源文件名
        vm.UndoCommand.Execute(null);
        Assert.Equal("photo_修复", vm.SuggestFileName(vm.CurrentNode!));

        vm.UndoCommand.Execute(null);
        Assert.Equal("photo", vm.SuggestFileName(vm.CurrentNode!));
    }

    [AvaloniaFact]
    public void SuggestFileName_无源文件名时时间戳兜底()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1));
        Assert.StartsWith("Inpaint_", vm.SuggestFileName(vm.CurrentNode!));

        vm.PushHistory(MakeBitmap(2, 2), Translations.Instance.NodeInpaint);
        Assert.Equal("修复", vm.SuggestFileName(vm.CurrentNode!));
    }

    [AvaloniaFact]
    public async Task LoadFromStreamAsync_源文件名传入AdoptBitmap供保存建议()
    {
        // 回归：生产路径（打开/拖拽）都经 LoadFromStreamAsync 载入，
        // sourceName 必须一路传到 AdoptBitmap，不能半路丢掉
        var vm = new MainWindowViewModel(null, null);
        var source = MakeBitmap(2, 2);
        using var encoded = new MemoryStream();
        source.Save(encoded);
        encoded.Position = 0;

        await vm.LoadFromStreamAsync(encoded, "photo.jpg");

        Assert.True(vm.HasImage);
        Assert.Equal("photo", vm.SuggestFileName(vm.CurrentNode!));
    }

    [AvaloniaFact]
    public void SuggestFileName_已知目录重名自动递增()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inpaint-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "photo.png"), "");
            File.WriteAllText(Path.Combine(dir, "photo_修复.png"), "");
            var vm = new MainWindowViewModel(null, null);
            vm.AdoptBitmap(MakeBitmap(1, 1), Path.Combine(dir, "photo.jpg"));

            // 目录里已有 photo.png：原图建议名避让为 photo(2)
            var root = vm.CurrentNode!;
            Assert.Equal("photo(2)", vm.SuggestFileName(root));

            vm.PushHistory(MakeBitmap(2, 2), Translations.Instance.NodeInpaint);
            // photo_修复.png 已存在：递增为 photo_修复(2)，(2) 也被占时继续递增
            var generated = vm.CurrentNode!;
            Assert.Equal("photo_修复(2)", vm.SuggestFileName(generated));

            File.WriteAllText(Path.Combine(dir, "photo_修复(2).png"), "");
            Assert.Equal("photo_修复(3)", vm.SuggestFileName(generated));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public void SuggestFileName_目录未知时不探测()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1), "photo.jpg"); // 裸文件名，无目录信息
        Assert.Equal("photo", vm.SuggestFileName(vm.CurrentNode!));
    }

    [AvaloniaFact]
    public void AdoptBitmap_重新载入时旧源文件名不残留()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(1, 1), "old.png");
        Assert.Equal("old", vm.SuggestFileName(vm.CurrentNode!));

        vm.AdoptBitmap(MakeBitmap(2, 2));
        Assert.StartsWith("Inpaint_", vm.SuggestFileName(vm.CurrentNode!));
    }

    // ---- 超大图性能保护：历史字节预算、修复/超分像素上限、繁忙期忽略载入 ----

    [AvaloniaFact]
    public void PruneHistory_超字节预算_裁剪最旧节点且不受节点数限制()
    {
        var vm = new MainWindowViewModel(null, null);
        // 6 张 100×100（每张 40_000 字节）+ 根 4 字节 = 240_004 > 180_000；
        // MaxHistory=25 不会触发，纯字节预算生效
        vm.HistoryByteBudget = 180_000;
        vm.AdoptBitmap(MakeBitmap(1, 1));
        for (int i = 0; i < 6; i++)
            vm.PushHistory(MakeBitmap(100, 100), $"测试{i}");

        // 裁掉最旧两张链上节点（均为当前路径祖先，走拼接兜底）后 160_004 达标
        Assert.Equal(5, vm.HistoryNodes.Count);
        Assert.Equal(new PixelSize(1, 1), vm.HistoryNodes[0].Image.PixelSize);
        Assert.Null(vm.HistoryNodes[0].Parent);
        Assert.Equal(new PixelSize(100, 100), vm.CurrentImage!.PixelSize);
        // 拼接后撤销链完整：4 次撤销回到原图
        for (int i = 0; i < 4; i++)
        {
            Assert.True(vm.UndoCommand.CanExecute(null), $"第 {i + 1} 次撤销应可用");
            vm.UndoCommand.Execute(null);
        }
        Assert.Equal(new PixelSize(1, 1), vm.CurrentImage!.PixelSize);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void InpaintUpscale_超像素上限_写入状态且不进入繁忙()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.AdoptBitmap(MakeBitmap(6, 4)); // 24 像素
        vm.InpaintMaxPixels = 10;
        vm.UpscaleMaxPixels = 10;

        // guard 在置位 IsBusy 之前返回（同步段），命令执行完立即断言
        vm.InpaintCommand.Execute(null);
        Assert.StartsWith("图片过大", vm.StatusText);
        Assert.False(vm.IsBusy);

        vm.UpscaleCommand.Execute(null);
        Assert.StartsWith("图片过大", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task LoadFromStreamAsync_繁忙时忽略载入()
    {
        var vm = new MainWindowViewModel(null, null);
        vm.IsBusy = true;
        var source = MakeBitmap(2, 2);
        using var encoded = new MemoryStream();
        source.Save(encoded);
        encoded.Position = 0;

        await vm.LoadFromStreamAsync(encoded, "photo.jpg");

        Assert.False(vm.HasImage);
        Assert.Null(vm.StatusText);
    }
}
