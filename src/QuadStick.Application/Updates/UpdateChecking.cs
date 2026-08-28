namespace QuadStick.Application.Updates;

public sealed record UpdateResult(string Message, string? DownloadUrl, bool IsNewer);

public enum UpdateSourceStatus { Success, RateLimited, HttpFailure, NetworkFailure, InvalidResponse }

public sealed record UpdateRelease(
    UpdateSourceStatus Status,
    string Tag = "",
    string? PageUrl = null,
    bool Prerelease = false,
    int HttpStatus = 0);

public interface IUpdateReleaseSource
{
    Task<UpdateRelease> GetLatestAsync(CancellationToken cancellationToken = default);
}

public static class UpdateVersion
{
    public static string Normalize(string tag) =>
        tag.TrimStart().StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Trim()[1..] : tag.Trim();

    public static int Compare(string a, string b)
    {
        var left = a.Split('.');
        var right = b.Split('.');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            if (!int.TryParse(i < left.Length ? left[i] : "0", out var x)) return 0;
            if (!int.TryParse(i < right.Length ? right[i] : "0", out var y)) return 0;
            if (x != y) return x > y ? 1 : -1;
        }
        return 0;
    }
}

public sealed class CheckForUpdatesUseCase
{
    readonly IUpdateReleaseSource _source;

    public CheckForUpdatesUseCase(IUpdateReleaseSource source) => _source = source;

    public async Task<UpdateResult> ExecuteAsync(string current, CancellationToken cancellationToken = default)
    {
        var release = await _source.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        switch (release.Status)
        {
            case UpdateSourceStatus.RateLimited:
                return new UpdateResult(
                    "GitHub is asking us to wait before checking again. Try again in a few minutes.", null, false);
            case UpdateSourceStatus.HttpFailure:
                return new UpdateResult(
                    $"Could not check for updates (GitHub answered {release.HttpStatus}). You are on {current}.", null, false);
            case UpdateSourceStatus.NetworkFailure:
                return new UpdateResult(
                    $"Could not reach GitHub to check for updates. You are on {current}.", null, false);
            case UpdateSourceStatus.InvalidResponse:
                return new UpdateResult($"GitHub sent something we could not read. You are on {current}.", null, false);
            case UpdateSourceStatus.Success:
                break;
            default:
                throw new InvalidOperationException($"Unknown update source status: {release.Status}.");
        }

        var latest = UpdateVersion.Normalize(release.Tag);
        if (latest.Length == 0)
            return new UpdateResult($"GitHub did not name a release. You are on {current}.", null, false);

        if (UpdateVersion.Compare(latest, current) <= 0)
            return new UpdateResult($"You are on {current}, which is the latest.", release.PageUrl, false);

        var label = release.Prerelease ? $"{latest} (a preview release)" : latest;
        return new UpdateResult($"You are on {current}. {label} is out.", release.PageUrl, true);
    }
}