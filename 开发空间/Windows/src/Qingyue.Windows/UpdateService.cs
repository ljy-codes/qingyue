using System.Net.Http;
using System.Text.Json;

namespace Qingyue.Windows;

public sealed record UpdateCheckResult(
    bool IsAvailable,
    Version CurrentVersion,
    Version? LatestVersion,
    string? ReleaseUrl,
    DateTimeOffset? PublishedAt,
    string? Error);

public sealed class UpdateService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly Version currentVersion;
    private readonly HttpClient httpClient;
    private readonly Uri releasesUri;

    public UpdateService(Version currentVersion, HttpClient? httpClient = null, Uri? releasesUri = null)
    {
        this.currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        this.httpClient = httpClient ?? new HttpClient();
        this.releasesUri = releasesUri ??
            new Uri("https://api.github.com/repos/ljy-codes/qingyue/releases?per_page=20");
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, releasesUri);
            request.Headers.UserAgent.ParseAdd("Qingyue-Update-Check/1.0");
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(timeout.Token);
            return CheckJson(json);
        }
        catch (OperationCanceledException)
        {
            return Failed(cancellationToken.IsCancellationRequested ? "检查更新已取消。" : "检查更新超时。");
        }
        catch (JsonException)
        {
            return Failed("更新服务返回的数据格式不正确。");
        }
        catch (HttpRequestException error)
        {
            return Failed("无法连接更新服务：" + error.Message);
        }
        catch (Exception error) when (error is InvalidOperationException or UriFormatException)
        {
            return Failed("更新服务返回的数据不可用：" + error.Message);
        }
    }

    private UpdateCheckResult CheckJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("发布列表不是数组。");

        bool stableSeen = false;
        Release? latest = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            bool draft = GetBoolean(item, "draft");
            bool prerelease = GetBoolean(item, "prerelease");
            if (draft || prerelease) continue;

            string? tag = GetString(item, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) continue;
            if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var version)) continue;
            stableSeen = true;

            string? url = GetString(item, "html_url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var releaseUri) ||
                releaseUri.Scheme is not ("http" or "https"))
                continue;

            DateTimeOffset? publishedAt = null;
            if (DateTimeOffset.TryParse(GetString(item, "published_at"), out var published))
                publishedAt = published;
            var candidate = new Release(version, releaseUri.ToString(), publishedAt);
            if (latest is null || candidate.Version > latest.Version) latest = candidate;
        }

        if (latest is null)
            return Failed(stableSeen ? "稳定版本缺少有效发布页地址。" : "未找到可用的稳定版本。");

        return new(
            latest.Version > currentVersion,
            currentVersion,
            latest.Version,
            latest.Url,
            latest.PublishedAt,
            null);
    }

    private UpdateCheckResult Failed(string error) =>
        new(false, currentVersion, null, null, null, error);

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool GetBoolean(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private sealed record Release(Version Version, string Url, DateTimeOffset? PublishedAt);
}
