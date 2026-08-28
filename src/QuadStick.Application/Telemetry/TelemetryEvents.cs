namespace QuadStick.Application.Telemetry;

public enum TelemetryEvent
{
    AppLaunched, ProfileOpened, ProfileSaved,
    InstallAttempted, InstallSucceeded, InstallFailed,
    FeatureUsed, FeedbackSubmitted,
}

public enum ProfileSource { New, File, Library, Device, Sheets, Drive, Rescue }

public enum InstallFailure
{
    HasErrors, NoProfile, NotAQuadstick,
    CancelledDevice, CancelledFolder, CancelledDefault, CancelledPreferences, IoError,
}

public enum AppFeature { SheetsImport, DriveBackup, DriveRestore, ShareLink }

public sealed record TelemetryEventData(
    TelemetryEvent Event,
    ProfileSource? Source = null,
    InstallFailure? Failure = null,
    AppFeature? Feature = null);

/// <summary>Application-facing telemetry capability. Provider-specific event
/// serialization, privacy scrubbing and transport live in Infrastructure.</summary>
public interface ITelemetrySink
{
    bool Track(TelemetryEventData data);
}
