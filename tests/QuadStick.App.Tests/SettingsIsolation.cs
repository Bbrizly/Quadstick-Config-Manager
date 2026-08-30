using System;
using System.IO;
using System.Runtime.CompilerServices;
using QuadStick.App;

namespace QuadStick.App.Tests;

// Tests must not read or write the settings file of whoever is running them.
// Without this every run rewrote the developer's own model, theme and scale,
// and a test that failed to put one back left the next run reading it.
//
// A module initializer, like TelemetrySilence: it runs before the first test
// and before any static constructor a test touches, so there is no ordering
// left for someone to get wrong later.
internal static class SettingsIsolation
{
    [ModuleInitializer]
    internal static void Install() =>
        Settings.PathOverride = Path.Combine(
            Directory.CreateTempSubdirectory("qscm-tests-").FullName, "settings.json");
}
