using QuadStick.Application.Telemetry;

namespace QuadStick.Infrastructure.Telemetry;

/// <summary>
/// Provider adapter for Application's semantic telemetry port. The existing
/// Telemetry transport remains the single privacy boundary: consent gating,
/// property allowlists, scrubbing, install identity and PostHog wire names are
/// deliberately not duplicated here.
/// </summary>
public sealed class PostHogTelemetrySink : ITelemetrySink
{
    public bool Track(TelemetryEventData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // A semantic event has at most one supported discriminator today. Reject
        // contradictory data instead of silently choosing one and producing a
        // provider event the Application layer did not ask for.
        var detailCount = (data.Source is null ? 0 : 1)
                        + (data.Failure is null ? 0 : 1)
                        + (data.Feature is null ? 0 : 1);
        if (detailCount > 1) return false;

        if (data.Source is { } source)
            return Telemetry.Track(data.Event, source);
        if (data.Failure is { } failure)
            return Telemetry.Track(data.Event, failure);
        if (data.Feature is { } feature)
            return Telemetry.Track(data.Event, feature);
        return Telemetry.Track(data.Event);
    }
}