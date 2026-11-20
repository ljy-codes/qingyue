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
                "tag_name": "v0.9.0",
                "draft": false,
                "prerelease": false,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/v0.9.0",
                "published_at": "2026-11-15T10:00:00Z",
                "assets": []
              },
              {
                "tag_name": "v99.0.0-beta.1",
                "draft": false,
                "prerelease": true,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/v99.0.0-beta.1"
              },
              {
                "tag_name": "v100.0.0",
                "draft": true,
                "prerelease": false,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/v100.0.0"
              },
              {
                "tag_name": "bad-version",
                "draft": false,
                "prerelease": false,
                "html_url": "https://github.com/ljy-codes/qingyue/releases/tag/bad-version"
              }
            ]
            """;

        var service = Create(new Version(0, 8, 1), releases);
        var available = service.CheckAsync().GetAwaiter().GetResult();
        check(available.IsAvailable && available.LatestVersion == new Version(0, 9, 0),
            "更新检查选择最新稳定版本并排除预发布、草稿和坏版本");
        check(available.ReleaseUrl == "https://github.com/ljy-codes/qingyue/releases/tag/v0.9.0",
            "没有安装包附件时保留有效的 GitHub Release 页面");

        var current = Create(new Version(0, 9, 0), releases).CheckAsync().GetAwaiter().GetResult();
        check(!current.IsAvailable && current.Error is null, "当前已经是最新版本时不提示更新");

        var malformed = Create(new Version(0, 8, 1), "{not-json")
            .CheckAsync().GetAwaiter().GetResult();
        check(!malformed.IsAvailable && malformed.Error?.Contains("格式", StringComparison.Ordinal) == true,
            "更新接口返回坏 JSON 时返回可显示的错误");

        var noUrl = Create(new Version(0, 8, 1),
            """[{"tag_name":"v0.9.0","draft":false,"prerelease":false,"assets":[]} ]""")
            .CheckAsync().GetAwaiter().GetResult();
        check(!noUrl.IsAvailable && noUrl.Error?.Contains("发布页", StringComparison.Ordinal) == true,
            "稳定版本缺少发布页地址时不生成不可打开的更新");

        var timedOut = new UpdateService(new Version(0, 8, 1),
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

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true));
    }
}
