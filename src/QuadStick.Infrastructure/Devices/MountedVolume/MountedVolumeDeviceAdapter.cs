using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using QuadStick.Application.Devices;
using QuadStick.Format;

namespace QuadStick.Infrastructure.Devices.MountedVolume;

/// <summary>Mounted-filesystem implementation of QuadStick discovery/profile
/// install. Mount paths never escape as device identity: this adapter owns the
/// opaque DeviceId-to-root mapping for its lifetime.</summary>
public sealed class MountedVolumeDeviceAdapter :
    IDeviceDiscovery,
    IManualDeviceResolver,
    IDeviceProfileStore
{
    readonly ConcurrentDictionary<string, string> _roots = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<DeviceDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roots = await Task.Run(() => Device.FindCandidates(), cancellationToken).ConfigureAwait(false);
        return roots.Select(Describe).ToList();
    }

    public void InvalidateCache() => Device.InvalidateCandidateCache();

    public async Task<DeviceDescriptor?> ResolveMountedFolderAsync(
        string folder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var isTarget = await Task.Run(() => Device.IsInstallTarget(folder), cancellationToken).ConfigureAwait(false);
        return isTarget ? Describe(folder) : null;
    }

    public async Task<DeviceInstallReceipt> InstallAsync(
        DeviceId device,
        DeviceInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_roots.TryGetValue(device.Value, out var root))
            throw new InvalidOperationException("That QuadStick is no longer available. Refresh the device list and try again.");

        // Cancellation is honored before the destructive operation begins. Once
        // Device.Install starts it must finish verification/restore without an
        // injected cancellation halfway through the filesystem swap.
        var result = await Task.Run(() =>
        {
            if (!Device.IsInstallTarget(root))
                throw new InvalidOperationException("That folder no longer looks like a QuadStick (no default.csv at its root).");

            var profile = ProfileFile.Load(request.Payload.CsvText);
            var csvFileName = profile.Document.CsvFileName ?? "";
            if (!string.Equals(csvFileName, request.Payload.FileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The serialized profile file name does not match the install request.");

            return Device.Install(
                profile,
                root,
                Device.DefaultBackupDir(),
                request.AllowDefaultCsv,
                request.AllowPreferencesCsv);
        }, CancellationToken.None).ConfigureAwait(false);

        return new DeviceInstallReceipt(
            request.Payload.FileName,
            result.BackupPath is { Length: > 0 }
                ? new DeviceRecoveryReference(result.BackupPath)
                : null);
    }

    DeviceDescriptor Describe(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var id = new DeviceId(OpaqueId(normalized));
        _roots[id.Value] = normalized;
        return new DeviceDescriptor(
            id,
            DisplayName(normalized),
            DeviceTransportKind.MountedVolume,
            DeviceCapabilities.ProfileStorage,
            normalized);
    }

    static string OpaqueId(string normalizedRoot)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot));
        return "mounted-volume:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    static string DisplayName(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}