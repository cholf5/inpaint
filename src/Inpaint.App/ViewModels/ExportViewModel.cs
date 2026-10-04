using System.ComponentModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Inpaint.App.Localization;
using Inpaint.App.Services;

namespace Inpaint.App.ViewModels;

/// <summary>
/// 导出对话框：选格式（PNG/JPEG/WebP）+ 质量，松手后防抖编码整图并实时显示预估大小。
/// 预估即编码——确认保存时优先复用最近一次预估结果（参数一致），不再重复编码。
/// 编码器是 Skia（见 ImageExporter），大图上 WebP/JPEG 编码要数秒，全程在后台并支持取消；
/// 估算结果带代数（generation）标记，参数再变时旧结果直接丢弃。
/// </summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly Bitmap _source;
    private readonly Translations _t = Translations.Instance;

    /// <summary>防抖时长：拖动质量滑块时停止变化后才开始编码。internal 供单测置零。</summary>
    internal TimeSpan EstimateDebounce { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>估算执行替身（单测注入，不打真实编码器）；null 走 ImageExporter。internal 供单测。</summary>
    internal Func<ExportFormat, int, CancellationToken, Task<byte[]>>? EstimateExecutor;

    /// <summary>最近一次预估的异步任务（单测等待用）。internal 供单测。</summary>
    internal Task EstimateTask => _estimateTask;

    /// <summary>最近一次预估结果是否可复用为保存产物（参数一致时预估即编码）。internal 供单测。</summary>
    internal bool TryGetCachedBytes(ExportFormat format, int quality, out byte[] bytes)
    {
        if (_cachedKey == KeyFor(format, quality) && _cachedBytes is not null)
        {
            bytes = _cachedBytes;
            return true;
        }
        bytes = [];
        return false;
    }

    // 选项实例一次创建、跨语言复用（约定同 SettingsViewModel），顺序与 ExportFormat 枚举下标一一对应
    private readonly OptionItem[] _formatOptions = [new(""), new(""), new("")];
    private int _generation;
    private CancellationTokenSource _cts = new();
    private Task _estimateTask = Task.CompletedTask;
    private (ExportFormat Format, int Quality) _cachedKey = (0, 0);
    private byte[]? _cachedBytes;

    public ExportViewModel(Bitmap source) : this(source, ExportFormat.Png, ImageExporter.DefaultQuality)
    {
    }

    public ExportViewModel(Bitmap source, ExportFormat initialFormat, int initialQuality)
    {
        _source = source;
        _formatIndex = Math.Clamp((int)initialFormat, 0, 2);
        _quality = Math.Clamp(initialQuality, 1, 100);
        _isQualityVisible = Format != ExportFormat.Png;
        UpdateOptionLabels();
        Translations.Instance.PropertyChanged += OnLanguageChanged;
    }

    /// <summary>启动首轮预估（窗口 OnOpened 调用，测试可控起点）。internal 供单测。</summary>
    internal void StartInitialEstimate() => ScheduleEstimate();

    /// <summary>窗口关闭时取消在跑的预估并解除对全局 Translations 的订阅（约定同 SettingsViewModel）。</summary>
    public void Detach()
    {
        _generation++;
        _cts.Cancel();
        Translations.Instance.PropertyChanged -= OnLanguageChanged;
        SwapPreview(null);
    }

    /// <summary>对比预览的原图（待导出位图本身，生命周期归历史树，对话框只读）。</summary>
    public Bitmap PreviewOriginal => _source;

    /// <summary>按当前参数编码结果解码出的 1:1 预览（= 落盘内容）；null = 尚未生成。新预估到位时替换并释放旧图。</summary>
    [ObservableProperty] private Bitmap? _previewResult;

    public IReadOnlyList<OptionItem> FormatOptions => _formatOptions;

    [ObservableProperty]
    private int _formatIndex;

    /// <summary>当前格式（FormatIndex 与 ExportFormat 下标一一对应）。</summary>
    public ExportFormat Format => (ExportFormat)Math.Clamp(FormatIndex, 0, 2);

    partial void OnFormatIndexChanged(int value)
    {
        if (value < 0) return; // ComboBox 选区重置瞬间可能回写 -1（约定同 SettingsViewModel），忽略
        IsQualityVisible = (ExportFormat)value != ExportFormat.Png;
        ScheduleEstimate();
    }

    [ObservableProperty]
    private int _quality = ImageExporter.DefaultQuality;

    /// <summary>Slider 的 Value 是 double，经此代理映射到 int 质量（1..100）。</summary>
    public double QualitySlider
    {
        get => Quality;
        set => Quality = (int)Math.Clamp(Math.Round(value), 1, 100);
    }

    partial void OnQualityChanged(int value)
    {
        // PNG 无损，质量不影响结果，不重跑预估
        if (Format == ExportFormat.Png) return;
        ScheduleEstimate();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimateStatus))]
    private bool _isQualityVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimateStatus))]
    private bool _isEstimating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimateStatus))]
    private string? _estimatedSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimateStatus))]
    private string? _estimateError;

    /// <summary>估算状态行：错误 > 估算中 > 预估大小（随语言切换刷新，属派生文本）。</summary>
    public string EstimateStatus =>
        EstimateError is { } error ? error
        : IsEstimating ? _t.Estimating
        : EstimatedSize is { } size ? string.Format(_t.EstimatedSizeFormat, size)
        : "";

    /// <summary>确认进行中：期间禁用确认按钮（取消估算 + 编码可能要数秒）。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _isConfirming;

    /// <summary>确认导出：取消在跑的预估，参数命中缓存直接复用，否则现编码一次。</summary>
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        IsConfirming = true;
        try
        {
            _generation++; // 使在跑的预估过期，避免旧结果晚到覆盖状态
            _cts.Cancel();
            var format = Format;
            var quality = Math.Clamp(Quality, 1, 100);
            var bytes = TryGetCachedBytes(format, quality, out var cached)
                ? cached
                : await Task.Run(() => ImageExporter.Encode(_source, format, quality));
            ExportConfirmed?.Invoke(new ExportChoice(format, quality, bytes));
        }
        catch (Exception e)
        {
            EstimateError = string.Format(_t.ExportFailed, e.Message);
        }
        finally
        {
            IsEstimating = false;
            IsConfirming = false;
        }
    }

    private bool CanConfirm() => !IsConfirming;

    /// <summary>确认导出事件：窗口订阅并以对话框结果形式关闭。</summary>
    public event Action<ExportChoice>? ExportConfirmed;

    /// <summary>参数键：PNG 无损，质量不参与键（来回调质量命中同一份缓存）。</summary>
    private (ExportFormat Format, int Quality) KeyFor(ExportFormat format, int quality) =>
        format == ExportFormat.Png ? (format, 0) : (format, quality);

    /// <summary>重排预估：取消旧任务；参数命中缓存时直接显示（例如来回拖质量滑块），否则后台防抖编码。</summary>
    private void ScheduleEstimate()
    {
        var key = KeyFor(Format, Quality);
        _generation++;
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        if (TryGetCachedBytes(key.Format, key.Quality, out var cached))
        {
            EstimateError = null;
            EstimatedSize = ImageExporter.FormatBytes(cached.Length);
            IsEstimating = false;
            return;
        }
        IsEstimating = true;
        _estimateTask = EstimateAsync(_generation, key, _cts.Token);
    }

    /// <summary>UI 线程启动：防抖 → 线程池编码 → 回 UI 线程写状态与缓存（约定同主 ViewModel 的 Task.Run/IProgress）。</summary>
    private async Task EstimateAsync(int generation, (ExportFormat Format, int Quality) key, CancellationToken ct)
    {
        try
        {
            if (EstimateDebounce > TimeSpan.Zero) await Task.Delay(EstimateDebounce, ct);
            ct.ThrowIfCancellationRequested();
            var bytes = EstimateExecutor is { } executor
                ? await executor(key.Format, key.Quality, ct)
                : await Task.Run(() => ImageExporter.Encode(_source, key.Format, key.Quality), ct);
            if (generation != _generation || ct.IsCancellationRequested) return; // 参数已变，结果过期
            _cachedKey = key;
            _cachedBytes = bytes;
            EstimateError = null;
            EstimatedSize = ImageExporter.FormatBytes(bytes.Length);
            // 大小先上屏，再解码真实字节做 1:1 对比预览（预览 = 最终落盘内容）；失败保留旧预览
            var preview = await Task.Run(() => DecodePreview(bytes));
            if (generation != _generation || ct.IsCancellationRequested)
            {
                preview?.Dispose();
                return;
            }
            SwapPreview(preview);
            IsEstimating = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (generation != _generation) return;
            EstimateError = string.Format(_t.ExportFailed, e.Message);
            IsEstimating = false;
        }
    }

    /// <summary>把预估字节解码成 1:1 对比预览；解码失败返回 null（保留旧预览）。</summary>
    private static Bitmap? DecodePreview(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>替换预览位图：先换引用再释放旧图（同线程绑定同步切换，旧图随即无引用）。UI 线程调用。</summary>
    private void SwapPreview(Bitmap? next)
    {
        var old = PreviewResult;
        PreviewResult = next;
        old?.Dispose();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateOptionLabels();
        OnPropertyChanged(nameof(EstimateStatus));
    }

    private void UpdateOptionLabels()
    {
        _formatOptions[0].Label = _t.PngFileType;
        _formatOptions[1].Label = _t.JpegFileType;
        _formatOptions[2].Label = _t.WebPFileType;
    }
}
