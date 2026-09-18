using System.Text.RegularExpressions;

namespace QuadStick.App;

internal static class RegistryDeepLink
{
    static readonly Regex ProfileId = new("^[a-z0-9]+(?:-+[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    static readonly Regex CanonicalUri = new("^qcm://profile/[a-z0-9]+(?:-+[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryGetProfileId(Uri? uri, out string profileId)
    {
        profileId = "";
        if (uri is null || !uri.IsAbsoluteUri) return false;
        // Validate the original spelling before System.Uri normalizes dot
        // segments. Otherwise qcm://profile/../../bad can become /bad first.
        if (!CanonicalUri.IsMatch(uri.OriginalString)) return false;
        if (!uri.Scheme.Equals("qcm", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("profile", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var candidate = uri.AbsolutePath.Trim('/');
        if (!ProfileId.IsMatch(candidate)) return false;
        profileId = candidate;
        return true;
    }

    public static bool TryGetProfileId(IReadOnlyList<string>? args, out string profileId)
    {
        profileId = "";
        if (args is null) return false;
        foreach (var arg in args)
            if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) && TryGetProfileId(uri, out profileId))
                return true;
        return false;
    }
}
