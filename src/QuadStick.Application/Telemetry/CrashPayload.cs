namespace QuadStick.Application.Telemetry;

// Neutral crash-report DTOs shared by presentation-side crash capture and the
// Infrastructure telemetry provider. They contain only explicitly permitted
// fields; provider SDK types and filesystem concerns never enter them.
public sealed record CrashFrame(string Function, bool InApp);

public sealed record CrashException(string Type, IReadOnlyList<CrashFrame> Frames);

public sealed record CrashPayload(
    int Schema,
    string Where,
    string App,
    string Os,
    string OsVersion,
    bool IsDebug,
    string Utc,
    IReadOnlyList<CrashException> Chain);
