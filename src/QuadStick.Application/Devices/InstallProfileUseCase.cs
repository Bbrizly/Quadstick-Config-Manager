using QuadStick.Format;

namespace QuadStick.Application.Devices;

public enum InstallProfileStatus
{
    Installed,
    HasErrors,
    ConfirmationRequiredDefault,
    ConfirmationRequiredPreferences,
}

public sealed record InstallProfileResult(
    InstallProfileStatus Status,
    DeviceInstallReceipt? Receipt = null);

/// <summary>Application-level install policy. The UI-owned editor is snapped
/// before this use case is invoked; only immutable data crosses the async
/// boundary. Infrastructure revalidates the actual device immediately before
/// any destructive mounted-volume work.</summary>
public sealed class InstallProfileUseCase
{
    readonly IDeviceProfileStore _store;

    public InstallProfileUseCase(IDeviceProfileStore store) => _store = store;

    public async Task<InstallProfileResult> ExecuteAsync(
        ProfileSnapshot profile,
        DeviceId device,
        bool confirmDefaultCsv,
        bool confirmPreferencesCsv,
        string? sourceSheetId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.HasErrors)
            return new InstallProfileResult(InstallProfileStatus.HasErrors);

        var fileName = profile.CsvFileName ?? "config.csv";
        if (string.Equals(fileName, "default.csv", StringComparison.OrdinalIgnoreCase) && !confirmDefaultCsv)
            return new InstallProfileResult(InstallProfileStatus.ConfirmationRequiredDefault);

        if (string.Equals(fileName, "prefs.csv", StringComparison.OrdinalIgnoreCase) && !confirmPreferencesCsv)
            return new InstallProfileResult(InstallProfileStatus.ConfirmationRequiredPreferences);

        var csv = DeviceProfileSerializer.Serialize(
            profile,
            new ProfileSerializationContext(sourceSheetId));
        var request = new DeviceInstallRequest(
            new DeviceProfilePayload(fileName, csv),
            confirmDefaultCsv,
            confirmPreferencesCsv);

        var receipt = await _store.InstallAsync(device, request, cancellationToken).ConfigureAwait(false);
        return new InstallProfileResult(InstallProfileStatus.Installed, receipt);
    }
}