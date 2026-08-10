using System.Reflection;
using System.Text.Json;

namespace QuadStick.App;

/// <summary>What a check found, already in the words the settings line shows.
/// There is no "unknown": every outcome, including a failed one, says
/// something the user can act on.</summary>
public sealed record UpdateResult(string Message, string? DownloadUrl, bool IsNewer);

// Ask GitHub what the newest release is and say whether this copy is behind.
// Nothing is downloaded and nothing is replaced: the button opens the release
// page in a browser. Replacing the binary needs code signing first, and an
// unsigned self-update is how you teach people to click through a warning.
public static class UpdateCheck
{
    const string LatestUrl = "https://api.github.com/repos/Bbrizly/Quadstick-Config-Manager/releases/latest";

    /// <summary>The running version, "1.6.0".</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<UpdateResult> LatestAsync(HttpClient http, string current, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            // GitHub answers 403 to a request with no user agent.
            req.Headers.TryAddWithoutValidation("User-Agent", "QuadStickConfigManager");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct);

            // The rate limit is its own answer: the check failed for a reason
            // that has nothing to do with the user and passes on its own.
            if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
                return new UpdateResult(
                    "GitHub is asking us to wait before checking again. Try again in a few minutes.", null, false);
            if (!resp.IsSuccessStatusCode)
                return new UpdateResult(
                    $"Could not check for updates (GitHub answered {(int)resp.StatusCode}). "
                    + $"You are on {current}.", null, false);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var page = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            bool prerelease = root.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            var latest = Normalize(tag);

            if (latest.Length == 0)
                return new UpdateResult($"GitHub did not name a release. You are on {current}.", null, false);

            int order = Compare(latest, current);
            if (order <= 0)
                return new UpdateResult($"You are on {current}, which is the latest.", page, false);

            var label = prerelease ? $"{latest} (a preview release)" : latest;
            return new UpdateResult($"You are on {current}. {label} is out.", page, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new UpdateResult(
                $"Could not reach GitHub to check for updates. You are on {current}.", null, false);
        }
        catch (JsonException)
        {
            return new UpdateResult($"GitHub sent something we could not read. You are on {current}.", null, false);
        }
    }

    // "v1.6.1" -> "1.6.1". Anything after the numbers is left where it is, so a
    // tag this does not understand compares as text and never claims an update
    // that is not there.
    static string Normalize(string tag) =>
        tag.TrimStart().StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Trim()[1..] : tag.Trim();

    /// <summary>Newest-first ordering of two dotted versions. Positive when
    /// <paramref name="a"/> is newer. A part that is not a number stops the
    /// comparison there and counts as equal, so an odd tag never reads as
    /// newer than a real one.</summary>
    public static int Compare(string a, string b)
    {
        var left = a.Split('.');
        var right = b.Split('.');
        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            if (!int.TryParse(i < left.Length ? left[i] : "0", out var x)) return 0;
            if (!int.TryParse(i < right.Length ? right[i] : "0", out var y)) return 0;
            if (x != y) return x > y ? 1 : -1;
        }
        return 0;
    }
}
