using System;
using System.Runtime.CompilerServices;

namespace QuadStick.App.Tests;

// A test run must never be able to send. This does not rely on any fixture:
// a module initializer runs before the first test in the assembly, before
// TestAppBuilder, and before any static constructor a test touches, so there
// is no ordering left for someone to get wrong later.
//
// It is the second of two locks. The first is structural: without the
// gitignored TelemetryToken.Local.cs the project token is empty and
// Telemetry.Start refuses to build a client at all. This one covers the
// developer machine, where that file does exist.
internal static class TelemetrySilence
{
    [ModuleInitializer]
    internal static void Install() =>
        Environment.SetEnvironmentVariable("QSCM_TELEMETRY", "0");
}
