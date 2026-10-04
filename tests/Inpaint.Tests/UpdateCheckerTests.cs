using System.Net;
using Inpaint.App.Services;

namespace Inpaint.Tests;

/// <summary>检查更新的版本比较、Release JSON 解析与网络失败兜底（假 handler，不打真实网络）。</summary>
public class UpdateCheckerTests
{
    internal const string LatestReleaseJson =
        """{"tag_name":"v1.2.3","html_url":"https://github.com/cholf5/inpaint/releases/tag/v1.2.3"}""";

    // ---- Evaluate（解析 + 比较）----

    [Fact]
    public void Evaluate_最新版更高_发现更新()
    {
        var result = UpdateChecker.Evaluate(LatestReleaseJson, "v1.0.0");

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("v1.2.3", result.LatestVersion);
        Assert.Equal("https://github.com/cholf5/inpaint/releases/tag/v1.2.3", result.ReleaseUrl);
    }

    [Fact]
    public void Evaluate_本地不落后_已是最新()
    {
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateChecker.Evaluate(LatestReleaseJson, "v1.2.3").Outcome);
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateChecker.Evaluate(LatestReleaseJson, "v9.9.9").Outcome);
    }

    [Theory]
    [InlineData("v1.0.10", "v1.0.9")] // 数值比较而非字典序
    [InlineData("1.2", "v1.1.9")] // v 前缀可省、缺段补 0
    [InlineData("v2.0.0-beta", "v1.9.9")] // 段尾非数字后缀忽略
    public void Evaluate_版本号形态各异_仍正确识别更新(string tag, string current)
    {
        var result = UpdateChecker.Evaluate($$"""{"tag_name":"{{tag}}"}""", current);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        // 缺 html_url 时兜底到 Release 列表页
        Assert.Equal(UpdateChecker.ReleasesPageUrl, result.ReleaseUrl);
    }

    [Fact]
    public void Evaluate_tag无法识别版本_失败并带上原文()
    {
        var result = UpdateChecker.Evaluate("""{"tag_name":"nightly-2024"}""", "v1.0.0");

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal("nightly-2024", result.Error);
    }

    [Fact]
    public void Evaluate_响应不是JSON_失败()
    {
        var result = UpdateChecker.Evaluate("<html>rate limited</html>", "v1.0.0");

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("v", false)]
    [InlineData("abc", false)]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2", true)]
    public void TryParseVersion_形态识别(string? tag, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.TryParseVersion(tag, out _));
    }

    // ---- CheckAsync（HTTP 路径，假 handler）----

    [Fact]
    public async Task CheckAsync_请求带UserAgent且URL正确()
    {
        Uri? requested = null;
        string? userAgent = null;
        var checker = new UpdateChecker(new FakeHandler(request =>
        {
            requested = request.RequestUri;
            userAgent = request.Headers.UserAgent.ToString();
            return JsonResponse(LatestReleaseJson);
        }), currentVersion: "v1.0.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Uri(UpdateChecker.LatestReleaseApiUrl), requested);
        // GitHub API 缺 User-Agent 直接 403，这里守住请求头
        Assert.Equal("inpaint-desktop/1.0.0", userAgent);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task CheckAsync_网络失败_转成Failed不抛()
    {
        var checker = new UpdateChecker(
            new FakeHandler(_ => throw new HttpRequestException("offline")), currentVersion: "v1.0.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal("offline", result.Error);
    }

    internal static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };
}

/// <summary>同步假 HTTP handler：按请求返回定制响应。internal 供检查更新的 VM 测试复用。</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}
