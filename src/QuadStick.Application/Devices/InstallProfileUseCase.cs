using QuadStick.Format;

namespace QuadStick.Application.Devices;

public enum InstallProfileStatus
{
    Installed,
    HasErrors,
    InvalidTarget,
    ConfirmationRequiredDefault,
    ConfirmationRequiredPreferences,
}

public sealed record InstallProfileResult(
    InstallProfileStatus Status,
    DeviceInstallReceipt? Receipt = null);

/// <summary>
/// Application-level install policy. The mounted-volume adapter repeats its own
/// destructive-boundary checks; this layer makes safety independent of the UI.
/// </summary>
public sealed class InstallProfileUseCase
{
    readonly IDeviceProfileStore _store;

    public InstallProfileUseCase(IDeviceProfileStore store) => _store = store;

    public bool IsInstallTarget(DeviceId device) => _store.IsInstallTarget(device);

    public async Task<InstallProfileResult> ExecuteAsync(
        ProfileFile profile,
        DeviceId device,
        bool confirmDefaultCsv,
        bool confirmPreferencesCsv,
        CancellationToken cancellationToken = default)
    {
        profile.Reparse();
        if (profile.HasErrors)
            return new InstallProfileResult(InstallProfileStatus.HasErrors);

        if (!_store.IsInstallTarget(device))
            return new InstallProfileResult(InstallProfileStatus.InvalidTarget);

        if (profile.Document.IsDefaultConfig && !confirmDefaultCsv)
            return new InstallProfileResult(InstallProfileStatus.ConfirmationRequiredDefault);

        if (profile.Document.IsDevicePreferences && !confirmPreferencesCsv)
            return new InstallProfileResult(InstallProfileStatus.ConfirmationRequiredPreferences);

        var receipt = await _store.InstallAsync(
            profile, device, confirmDefaultCsv, confirmPreferencesCsv, cancellationToken)
            .ConfigureAwait(false);
        return new InstallProfileResult(InstallProfileStatus.Installed, receipt);
    }
}
