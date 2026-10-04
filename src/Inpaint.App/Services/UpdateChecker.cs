using System.Net;

namespace Inpaint.App.Services;

/// <summary>一次检查更新的结论。</summary>
public enum UpdateCheckOutcome
{
    /// <summary>本地版本不落后于最新 Release。</summary>
    UpToDate,

    /// <summary>发现更新的 Release（LatestVersion/ReleaseUrl 有值）。</summary>
    UpdateAvailable,

    /// <summary>网络/解析失败（原因在 Error，可能是无网、被拦截或版本号无法识别）。</summary>
    Failed,
}

/// <summary>Failed 时的错误归类：VM 据此选文案（HTTP 状态给友好提示，其余原样展示诊断信息）。</summary>
public enum UpdateCheckErrorKind
{
    /// <summary>非 Failed 结果。</summary>
    None,

    /// <summary>服务器返回了非成功状态码（Error = "HTTP 403" 形态的短诊断）。</summary>
    HttpStatus,

    /// <summary>网络层失败（无网/DNS/超时，Error = 原始异常消息）。</summary>
    Network,

    /// <summary>连通但响应里没有可识别的版本信息（代理拦截页、端点改版等）。</summary>
    InvalidResponse,
}

/// <summary>检查更新结果：UpdateAvailable 时 LatestVersion/ReleaseUrl 有值，Failed 时 Error/ErrorKind 有值。</summary>
public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string? LatestVersion = null,
    string? ReleaseUrl = null,
    string? Error = null,
    UpdateCheckErrorKind ErrorKind = UpdateCheckErrorKind.None);

/// <summary>
/// 检查更新：请求 GitHub 网页端 latest Release 重定向，与本地程序集版本按三段数值比较（tag 去 v 前缀）。
/// 刻意不走 api.github.com——未认证 API 限流 60 次/小时/按 IP 计，代理/CGNAT 出口被共享时几乎必然 403；
/// 网页端 releases/latest 回 302，Location 即 Release 页地址，从中抠 tag 即可，不必跟随重定向下载整页。
/// 不自定义代理管线：HttpClientHandler 默认读取 https_proxy 等环境变量，与模型下载的代理兜底行为一致。
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>latest Release 重定向端点（GitHub 网页，无 API 限流）。</summary>
    public const string LatestReleaseUrl = "https://github.com/cholf5/inpaint/releases/latest";

    /// <summary>Release 列表页兜底地址（检查失败时的跳转逃生门）。</summary>
    public const string ReleasesPageUrl = "https://github.com/cholf5/inpaint/releases";

    /// <summary>当前应用版本（v 前缀 + 三段），与「关于」页展示同源：Inpaint.App 程序集版本。</summary>
    public static string CurrentVersion { get; } =
        "v" + (typeof(UpdateChecker).Assembly.GetName().Version is { } version
            ? version.ToString(3)
            : "0.0.0");

    /// <summary>相对 Location 的补全基准（CheckAsync 固定请求此地址）。</summary>
    private static readonly Uri LatestReleaseBaseUri = new(LatestReleaseUrl, UriKind.Absolute);

    private readonly HttpMessageHandler? _handler;
    private readonly string _currentVersion;

    /// <summary>handler 供单测注入假响应；currentVersion 覆盖默认比较基准（User-Agent 与 UpToDate 状态行的版本随之同源，避免静态程序集版本与注入基准脱节）。</summary>
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
            using var response = await client.GetAsync(LatestReleaseUrl, ct);
            var location = response.Headers.Location is { IsAbsoluteUri: false } relative
                ? new Uri(LatestReleaseBaseUri, relative) // 相对 Location 按请求地址补全
                : response.Headers.Location;
            if (location is null)
            {
                response.EnsureSuccessStatusCode(); // 错误码转 HttpStatus 类失败
                return new UpdateCheckResult(UpdateCheckOutcome.Failed,
                    Error: "no release redirect", ErrorKind: UpdateCheckErrorKind.InvalidResponse);
            }
            return Evaluate(location.ToString(), _currentVersion);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // 统一转成结果而非向 UI 抛异常：无网/超时/HTTP 错误走 Failed，消息写入状态行
            return ToFailed(e);
        }
    }

    /// <summary>HTTP 状态码失败收敛成短诊断（"HTTP 403"），网络层失败保留原始异常消息。</summary>
    private static UpdateCheckResult ToFailed(Exception e) =>
        e is HttpRequestException { StatusCode: { } status }
            ? new UpdateCheckResult(UpdateCheckOutcome.Failed,
                Error: $"HTTP {(int)status}", ErrorKind: UpdateCheckErrorKind.HttpStatus)
            : new UpdateCheckResult(UpdateCheckOutcome.Failed,
                Error: e.Message, ErrorKind: UpdateCheckErrorKind.Network);

    private HttpClient CreateClient()
    {
        // 关闭自动重定向：302 的 Location 就够用，跟随会白下载整页 HTML。注入 handler 的单测路径本就不跟随
        var client = _handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"inpaint-desktop/{_currentVersion.TrimStart('v')}");
        return client;
    }

    /// <summary>本实例的比较基准（v 前缀三段）；UpToDate 状态行展示它而非静态程序集版本，单测注入后断言才能自洽。</summary>
    public string Version => _currentVersion;

    /// <summary>从 Release 页地址提取 tag 并与本地版本比较。internal 供单测。</summary>
    internal static UpdateCheckResult Evaluate(string? releaseUrl, string currentVersion)
    {
        var tag = ExtractTag(releaseUrl);
        if (!TryParseVersion(tag, out var latest))
            return new UpdateCheckResult(UpdateCheckOutcome.Failed,
                Error: tag ?? "no release redirect", ErrorKind: UpdateCheckErrorKind.InvalidResponse);

        TryParseVersion(currentVersion, out var current); // 解析失败按 0.0.0，任何 Release 都视为更新
        var outcome = Compare(latest, current) > 0 ? UpdateCheckOutcome.UpdateAvailable : UpdateCheckOutcome.UpToDate;
        return new UpdateCheckResult(outcome, LatestVersion: tag, ReleaseUrl: releaseUrl);
    }

    /// <summary>取 URL 路径最后一段（…/releases/tag/&lt;tag&gt; → &lt;tag&gt;）；非 URL 原样返回供诊断展示。</summary>
    internal static string? ExtractTag(string? releaseUrl)
    {
        if (string.IsNullOrWhiteSpace(releaseUrl)) return null;
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var uri)) return releaseUrl;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = segments.LastOrDefault(s => s.Length > 0);
        return last is null ? null : Uri.UnescapeDataString(last);
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
