#if !TELEMETRY_TOKEN_LOCAL
namespace QuadStick.Infrastructure.Telemetry;

// Placeholder PostHog project token. The real one lives in the gitignored
// TelemetryToken.Local.cs in this Infrastructure folder, and CI writes that
// file from a repo secret for official release builds.
//
// An empty token is not a broken build, it is a silent one: Telemetry.Start
// refuses to construct a client without one, so a source build, a fork, and a
// test run all send nothing at all. Only an official release can report.
//
// The token is write-only and ships inside the binary, so it is not a secret
// in the usual sense. Keeping it out of a public repo is about junk: anyone
// who greps it out can post events into the project and ruin the numbers it
// exists to produce.
static class TelemetryToken
{
    public const string Value = "";
}
#endif
