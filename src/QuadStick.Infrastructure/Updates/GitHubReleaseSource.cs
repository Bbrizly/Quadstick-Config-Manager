using System.Text.Json;
using QuadStick.Application.Updates;

namespace QuadStick.Infrastructure.Updates;

/// <summary>GitHub Releases implementation of the update provider port.</summary>
public sealed class GitHubReleaseSource : IUpdateReleaseSource
{
    const string LatestUrl = "https://api.github.com/repos/Bbrizly/Quadstick-Config-Manager/releases/latest";
    readonly HttpClient _http;

    public GitHubReleaseSource(HttpClient http) => _http = http;

    public async Task<UpdateRelease> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "QuadStickConfigManager");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);

            if ((int)resp.StatusCode is 403 or 429)
                return new UpdateRelease(UpdateSourceStatus.RateLimited, HttpStatus: (int)resp.StatusCode);
            if (!resp.IsSuccessStatusCode)
                return new UpdateRelease(UpdateSourceStatus.HttpFailure, HttpStatus: (int)resp.StatusCode);

            using var doc = JsonDocument.Parse(
                await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = doc.RootElement;
            return new UpdateRelease(
                UpdateSourceStatus.Success,
                root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "",
                root.TryGetProperty("html_url", out var page) ? page.GetString() : null,
                root.TryGetProperty("prerelease", out var preview) && preview.GetBoolean());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new UpdateRelease(UpdateSourceStatus.NetworkFailure);
        }
        catch (JsonException)
        {
            return new UpdateRelease(UpdateSourceStatus.InvalidResponse);
        }
    }
}