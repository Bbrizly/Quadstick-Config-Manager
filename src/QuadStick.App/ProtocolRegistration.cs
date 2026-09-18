using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuadStick.App;

internal static class ProtocolRegistration
{
    /// <summary>Best-effort per-user registration for unpackaged Windows builds.
    /// Store packages declare the same scheme in Package.appxmanifest.</summary>
    public static void EnsureCurrentUser()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\qcm");
            if (key is null) return;
            key.SetValue(null, "URL:QuadStick Config Manager Profile");
            key.SetValue("URL Protocol", "");
            using var command = key.CreateSubKey(@"shell\open\command");
            command?.SetValue(null, $"\"{executable}\" \"%1\"");
        }
        catch
        {
            // Protocol registration must never prevent QCM from starting.
        }
    }
}
