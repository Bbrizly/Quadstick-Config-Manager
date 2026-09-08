using Microsoft.Win32;

namespace QuadStick.App;

/// <summary>Register the qcm:// URL scheme for unpackaged Windows builds.
/// The Microsoft Store package and macOS bundle declare it in their manifests.
/// This is per-user, requires no administrator rights, and is deliberately
/// best-effort so startup can never fail because protocol registration did.</summary>
internal static class ProtocolRegistration
{
    public static void EnsureCurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\qcm");
            if (root is null) return;
            root.SetValue("", "URL:QuadStick Config Manager Profile");
            root.SetValue("URL Protocol", "");
            using var command = root.CreateSubKey(@"shell\open\command");
            command?.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch { }
    }
}
