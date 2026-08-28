using System.Reflection;

namespace QuadStick.App;

/// <summary>Presentation compatibility facade. Version comparison is Application
/// logic and GitHub HTTP is an Infrastructure adapter.</summary>
public static class UpdateCheck
{
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static Task<UpdateResult> LatestAsync(HttpClient http, string current, CancellationToken ct = default) =>
        new CheckForUpdatesUseCase(
            new QuadStick.Infrastructure.Updates.GitHubReleaseSource(http))
            .ExecuteAsync(current, ct);

    public static int Compare(string a, string b) => UpdateVersion.Compare(a, b);
}
