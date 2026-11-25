using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Qingyue.Windows;

internal static class UpdateServiceChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const string releases = """
            [
              {
                "tag_name": "v0.19.0-windows",
                "draft": false,
                "prerelease": false,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/v0.19.0-windows",
                "published_at": "2026-11-15T10:00:00Z",
                "assets": []
              },
              {
                "tag_name": "v99.0.0-beta.1-windows",
                "draft": false,
                "prerelease": true,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/v99.0.0-beta.1-windows"
              },
              {
                "tag_name": "v100.0.0-windows",
                "draft": true,
                "prerelease": false,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/v100.0.0-windows"
              },
              {
                "tag_name": "bad-version",
                "draft": false,
                "prerelease": false,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/bad-version"
              },
              {
                "tag_name": "v0.21.0",
                "draft": false,
                "prerelease": false,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/v0.21.0",
                "published_at": "2026-11-20T10:00:00Z"
              },
              {
                "tag_name": "v0.99.0-macos",
                "draft": false,
                "prerelease": false,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/v0.99.0-macos"
              }
            ]
            """;

        var service = Create(new Version(0, 18, 0), releases);
        var available = service.CheckAsync().GetAwaiter().GetResult();
        check(available.IsAvailable && available.LatestVersion == new Version(0, 21, 0),
            "更新检查识别历史裸版本和 Windows 平台版本，并选择最高版");
        check(available.ReleaseUrl == "https://github.com/ljy-codes/qingyue/releases/tag/v0.21.0",
            "裸版本仍必须指向精确匹配标签的官方 GitHub Release 页面");

        var current = Create(new Version(0, 21, 0), releases).CheckAsync().GetAwaiter().GetResult();
        check(!current.IsAvailable && current.Error is null, "当前已经是最新版本时不提示更新");

        const string historicalWindowsReleases = """
            [
              {
                "tag_name": "v0.18.0",
                "draft": false,
                "prerelease": false,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/v0.18.0"
              },
              {
                "tag_name": "v0.18.0-macos",
                "draft": false,
                "prerelease": false,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/v0.18.0-macos"
              }
            ]
            """;
        var historical = Create(new Version(0, 17, 0), historicalWindowsReleases)
            .CheckAsync().GetAwaiter().GetResult();
        check(historical.IsAvailable && historical.LatestVersion == new Version(0, 18, 0) &&
              historical.ReleaseUrl == "https://github.com/ljy-codes/qingyue/releases/tag/v0.18.0",
            "更新检查兼容 GitHub 上实际存在的裸 Windows 版本标签");
        var macOnly = Create(new Version(0, 17, 0), """
            [{"tag_name":"v9.0.0-macos","draft":false,"prerelease":false,
              "html_url":"https://github.com/ljy-codes/qingyue/releases/tag/v9.0.0-macos"}]
            """).CheckAsync().GetAwaiter().GetResult();
        check(!macOnly.IsAvailable && macOnly.LatestVersion is null,
            "更新检查不会把 macOS 包推荐给 Windows 用户");

        var malformed = Create(new Version(0, 18, 0), "{not-json")
            .CheckAsync().GetAwaiter().GetResult();
        check(!malformed.IsAvailable && malformed.Error?.Contains("格式", StringComparison.Ordinal) == true,
            "更新接口返回坏 JSON 时返回可显示的错误");

        string oversizedUtf8Json =
            $$"""[{"padding":"{{new string('中', 700_000)}}"}]""";
        check(oversizedUtf8Json.Length <= 2 * 1024 * 1024 &&
              Encoding.UTF8.GetByteCount(oversizedUtf8Json) > 2 * 1024 * 1024,
            "更新上限测试数据具备 UTF-16 字符数不超限、UTF-8 字节数超限的特征");
        var oversized = CreateRaw(
            new Version(0, 18, 0),
            Encoding.UTF8.GetBytes(oversizedUtf8Json),
            contentLength: null).CheckAsync().GetAwaiter().GetResult();
        check(!oversized.IsAvailable &&
              oversized.Error?.Contains("数据过大", StringComparison.Ordinal) == true,
            "未声明 Content-Length 的重叠多字节响应严格按 UTF-8 原始字节数拒绝");

        var empty = Create(new Version(0, 18, 0), "[]")
            .CheckAsync().GetAwaiter().GetResult();
        check(!empty.IsAvailable && empty.Error?.Contains("稳定版本", StringComparison.Ordinal) == true,
            "没有 Windows 稳定版本时返回可解释结果");

        var noUrl = Create(new Version(0, 18, 0),
            """[{"tag_name":"v0.19.0-windows","draft":false,"prerelease":false,"assets":[]}]""")
            .CheckAsync().GetAwaiter().GetResult();
        check(!noUrl.IsAvailable && noUrl.Error?.Contains("发布页", StringComparison.Ordinal) == true,
            "稳定版本缺少发布页地址时不生成不可打开的更新");

        foreach (var invalidTag in new[]
                 {
                     "v9.0.0-macos", "v9.0.0-windows-extra",
                     "0.9.0-windows", "V9.0.0-windows", "v9.0-windows",
                     "v9.0.0.0-windows", "windows-build-9"
                 })
        {
            string json = $$"""
                [{"tag_name":"{{invalidTag}}","draft":false,"prerelease":false,
                  "html_url":"https://github.com/ljy-codes/qingyue/releases/tag/{{invalidTag}}"}]
                """;
            var invalid = Create(new Version(0, 18, 0), json).CheckAsync().GetAwaiter().GetResult();
            check(!invalid.IsAvailable && invalid.LatestVersion is null,
                "拒绝非 Windows 平台或非严格版本标签：" + invalidTag);
        }

        foreach (var invalidUrl in new[]
                 {
                     "https://evil.invalid/releases/tag/v9.0.0-windows",
                     "http://github.com/ljy-codes/qingyue/releases/tag/v9.0.0-windows",
                     "https://github.com/ljy-codes/qingyue/releases/tag/v8.0.0-windows",
                     "https://github.com/ljy-codes/qingyue/releases/tag/v9.0.0-windows?redirect=1"
                 })
        {
            string json = $$"""
                [{"tag_name":"v9.0.0-windows","draft":false,"prerelease":false,
                  "html_url":"{{invalidUrl}}"}]
                """;
            var invalid = Create(new Version(0, 18, 0), json).CheckAsync().GetAwaiter().GetResult();
            check(!invalid.IsAvailable && invalid.LatestVersion is null,
                "拒绝非精确官方发布页：" + invalidUrl);
        }

        Uri? requestedUri = null;
        var defaultEndpoint = new UpdateService(
            new Version(0, 18, 0),
            new HttpClient(new CapturingHandler(releases, uri => requestedUri = uri)));
        _ = defaultEndpoint.CheckAsync().GetAwaiter().GetResult();
        check(requestedUri is not null &&
              requestedUri.Host == "api.github.com" &&
              requestedUri.AbsolutePath == "/repos/ljy-codes/qingyue/releases" &&
              requestedUri.Query.Contains("per_page=100", StringComparison.Ordinal),
            "默认更新接口请求 GitHub 前 100 个发布并限制平台标签");

        var timedOut = new UpdateService(new Version(0, 18, 0),
            new HttpClient(new TimeoutHandler()) { BaseAddress = new Uri("https://example.test") },
            new Uri("https://example.test/releases"));
        var timeout = timedOut.CheckAsync().GetAwaiter().GetResult();
        check(!timeout.IsAvailable && timeout.Error?.Contains("超时", StringComparison.Ordinal) == true,
            "网络取消或超时时返回错误而不是阻止主窗口");

        var settingsRoot = Path.Combine(Path.GetTempPath(), "qingyue-update-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(settingsRoot);
        try
        {
            var settings = new ReaderSettings { LastUpdateCheckUtc = DateTimeOffset.UtcNow };
            settings.Save(settingsRoot);
            var reloaded = ReaderSettings.Load(settingsRoot);
            check(reloaded.LastUpdateCheckUtc is not null, "更新检查时间可以写入并从 Settings.json 恢复");
        }
        finally
        {
            try { Directory.Delete(settingsRoot, true); } catch { }
        }
    }

    private static UpdateService Create(Version current, string json) =>
        new(current, new HttpClient(new JsonHandler(json)), new Uri("https://example.test/releases"));

    private static UpdateService CreateRaw(Version current, byte[] bytes, long? contentLength) =>
        new(current, new HttpClient(new RawContentHandler(bytes, contentLength)),
            new Uri("https://example.test/releases"));

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class RawContentHandler(byte[] bytes, long? contentLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentLength = contentLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }

    private sealed class CapturingHandler(string json, Action<Uri> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true));
    }
}
