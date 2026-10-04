using System.Text.Json;

namespace Inpaint.App.Services;

/// <summary>一次检查更新的结论。</summary>
public enum UpdateCheckOutcome
{
    /// <summary>本地版本不落后于最新 Release。</summary>
    UpToDate,

    /// <summary>发现更新的 Release（LatestVersion/ReleaseUrl 有值）。</summary>
    UpdateAvailable,

    /// <summary>网络/解析失败（原因在 Error，可能是无网、限流或版本号无法识别）。</summary>
    Failed,
}

/// <summary>检查更新结果：UpdateAvailable 时 LatestVersion/ReleaseUrl 有值，Failed 时 Error 有值。</summary>
public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string? LatestVersion = null,
    string? ReleaseUrl = null,
    string? Error = null);

/// <summary>
/// 检查更新：请求 GitHub latest Release，与本地程序集版本按三段数值比较（tag 去 v 前缀）。
/// API 强制要求 User-Agent（缺失直接 403）；未认证限额 60 次/小时/IP，手动 + 可选启动检查足够。
/// 不自定义 HttpMessageHandler：.NET 默认读取 https_proxy 等环境变量，与模型下载的代理兜底行为一致。
/// </summary>
public sealed class UpdateChecker
{
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/cholf5/inpaint/releases/latest";

    /// <summary>Release 页兜底地址（响应缺 html_url 时跳转用）。</summary>
    public const string ReleasesPageUrl = "https://github.com/cholf5/inpaint/releases";

    /// <summary>当前应用版本（v 前缀 + 三段），与「关于」页展示同源：Inpaint.App 程序集版本。</summary>
    public static string CurrentVersion { get; } =
        "v" + (typeof(UpdateChecker).Assembly.GetName().Version is { } version
            ? version.ToString(3)
            : "0.0.0");

    private static readonly string UserAgent = $"inpaint-desktop/{CurrentVersion.TrimStart('v')}";

    private readonly HttpMessageHandler? _handler;
    private readonly string _currentVersion;

    /// <summary>handler 供单测注入假响应；currentVersion 覆盖默认比较基准。</summary>
    public UpdateChecker(HttpMessageHandler? handler = null, string? currentVersion = null)
    {
        _handler = handler;
        _currentVersion = currentVersion ?? CurrentVersion;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(LatestReleaseApiUrl, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            return Evaluate(json, _currentVersion);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 统一转成结果而非向 UI 抛异常：无网/超时/限流走 Failed，消息写入状态行
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, Error: e.Message);
        }
    }

    private HttpClient CreateClient()
    {
        var client = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>解析 latest Release JSON 并与本地版本比较。internal 供单测。</summary>
    internal static UpdateCheckResult Evaluate(string json, string currentVersion)
    {
        string? tag = null, pageUrl = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind is JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("tag_name", out var t) && t.ValueKind is JsonValueKind.String)
                    tag = t.GetString();
                if (doc.RootElement.TryGetProperty("html_url", out var u) && u.ValueKind is JsonValueKind.String)
                    pageUrl = u.GetString();
            }
        }
        catch (JsonException)
        {
            // 响应不是 JSON（限流页/代理页等）：tag 为空，走下方 Failed
        }

        if (!TryParseVersion(tag, out var latest))
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, Error: tag ?? "invalid response");

        TryParseVersion(currentVersion, out var current); // 解析失败按 0.0.0，任何 Release 都视为更新
        var outcome = Compare(latest, current) > 0 ? UpdateCheckOutcome.UpdateAvailable : UpdateCheckOutcome.UpToDate;
        return new UpdateCheckResult(outcome, LatestVersion: tag, ReleaseUrl: pageUrl ?? ReleasesPageUrl);
    }

    /// <summary>解析 "v1.2.3" 形态的版本号为三段数值：v/V 前缀可省，缺段补 0，段尾非数字后缀（如 -beta）忽略。</summary>
    internal static bool TryParseVersion(string? tag, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var parts = tag.TrimStart('v', 'V').Split('.');
        if (parts.Length == 0 || !StartsWithDigits(parts[0], out var major)) return false;
        var minor = parts.Length > 1 && StartsWithDigits(parts[1], out var m) ? m : 0;
        var patch = parts.Length > 2 && StartsWithDigits(parts[2], out var p) ? p : 0;
        version = (major, minor, patch);
        return true;

        static bool StartsWithDigits(string segment, out int value)
        {
            value = 0;
            var digits = segment.TakeWhile(char.IsDigit).ToArray();
            // 超长数字串按无法识别处理，避免 int.Parse 溢出抛异常
            if (digits.Length == 0 || digits.Length > 9) return false;
            value = int.Parse(digits);
            return true;
        }
    }

    private static int Compare((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b) =>
        (a.Major, a.Minor, a.Patch).CompareTo((b.Major, b.Minor, b.Patch));
}
