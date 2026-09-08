using System.Text.RegularExpressions;

namespace QuadStick.App;

internal static class RegistryDeepLink
{
    static readonly Regex Id = new("^[a-z0-9]+(?:-+[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public static bool TryGetProfileId(IEnumerable<string>? args, out string profileId)
    {
        if (args is not null)
            foreach (var arg in args)
                if (Uri.TryCreate(arg, UriKind.Absolute, out var uri) && TryGetProfileId(uri, out profileId))
                    return true;
        profileId = "";
        return false;
    }

    public static bool TryGetProfileId(Uri? uri, out string profileId)
    {
        profileId = "";
        if (uri is null || !uri.Scheme.Equals("qcm", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("profile", StringComparison.OrdinalIgnoreCase)) return false;
        var candidate = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (candidate.Length is 0 or > 220 || !Id.IsMatch(candidate)) return false;
        profileId = candidate;
        return true;
    }
}
