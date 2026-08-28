using QuadStick.Application.Devices;
using QuadStick.Format;

namespace QuadStick.Infrastructure.Devices.MountedVolume;

/// <summary>
/// Mounted-filesystem implementation of QuadStick discovery/profile install.
/// DeviceId.Value is the mounted root only inside this adapter; Application and
/// presentation treat it as an opaque identity.
/// </summary>
public sealed class MountedVolumeDeviceAdapter : IDeviceDiscovery, IDeviceProfileStore
{
    public async Task<IReadOnlyList<DeviceDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roots = await Task.Run(() => Device.FindCandidates(), cancellationToken).ConfigureAwait(false);
        return roots.Select(root => new DeviceDescriptor(
            new DeviceId(root),
            DisplayName(root),
            DeviceTransportKind.MountedVolume,
            DeviceCapabilities.ProfileStorage,
            root)).ToList();
    }

    public void InvalidateCache() => Device.InvalidateCache();

    public bool IsInstallTarget(DeviceId device) => Device.IsInstallTarget(device.Value);

    public async Task<DeviceInstallReceipt> InstallAsync(
        ProfileFile profile,
        DeviceId device,
        bool confirmDefaultCsv,
        bool confirmPreferencesCsv,
        CancellationToken cancellationToken = default)
    {
        // Cancellation is honored before the destructive operation begins. Once
        // Device.Install starts, it must run through verification/restore to a
        // known safe state rather than being interrupted mid-swap.
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Task.Run(
            () => Device.Install(
                profile,
                device.Value,
                Device.DefaultBackupDir(),
                confirmDefaultCsv,
                confirmPreferencesCsv),
            CancellationToken.None).ConfigureAwait(false);
        return new DeviceInstallReceipt(result.InstalledPath, result.BackupPath);
    }

    static string DisplayName(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}
