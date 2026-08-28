using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using QuadStick.Application.Devices;
using QuadStick.Format;

namespace QuadStick.Infrastructure.Devices.MountedVolume;

/// <summary>Mounted-filesystem implementation of the profile-storage capability.
/// Mount paths never escape as device identity: this adapter owns the opaque
/// DeviceId-to-root mapping for its lifetime.</summary>
public sealed class MountedVolumeDeviceAdapter :
    IDeviceDiscovery,
    IManualDeviceResolver,
    IDeviceProfileStore
{
    public const long MaxProfileBytes = 4L * 1024 * 1024;

    readonly ConcurrentDictionary<string, string> _roots = new(StringComparer.Ordinal);
    readonly Func<IReadOnlyList<string>> _findRoots;

    public MountedVolumeDeviceAdapter(Func<IReadOnlyList<string>>? findRoots = null) =>
        _findRoots = findRoots ?? (() => Device.FindCandidates());

    public async Task<IReadOnlyList<DeviceDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roots = await Task.Run(_findRoots, cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<DeviceProfileGroup>> ListAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var groups = new List<DeviceProfileGroup>(descriptors.Count);
        foreach (var descriptor in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_roots.TryGetValue(descriptor.Id.Value, out var root))
            {
                groups.Add(new DeviceProfileGroup(descriptor, Array.Empty<DeviceProfileEntry>(),
                    "Could not resolve this QuadStick drive."));
                continue;
            }

            try
            {
                var profiles = await Task.Run(() => Directory.GetFiles(root, "*.csv")
                    .Select(Path.GetFileName)
                    .Where(name => name is not null && DeviceProfileRules.IsProfileFileName(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Select(name => new DeviceProfileEntry(
                        new DeviceProfileId(descriptor.Id, name!), name!))
                    .ToList(), cancellationToken).ConfigureAwait(false);
                groups.Add(new DeviceProfileGroup(descriptor, profiles));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                groups.Add(new DeviceProfileGroup(descriptor, Array.Empty<DeviceProfileEntry>(),
                    $"Could not read this drive: {ex.Message}"));
            }
        }
        return groups;
    }

    public Task<string> ReadAsync(
        DeviceProfileId profile,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveProfilePath(profile, requireInstallTarget: true);
            var length = new FileInfo(path).Length;
            if (length > MaxProfileBytes)
                throw new InvalidDataException($"{profile.FileName} is larger than the {MaxProfileBytes / (1024 * 1024)} MB profile safety limit.");
            return File.ReadAllText(path);
        }, cancellationToken);

    public async Task<DeviceInstallReceipt> InstallAsync(
        DeviceId device,
        DeviceInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_roots.TryGetValue(device.Value, out var root))
            throw new InvalidOperationException("That QuadStick is no longer available. Refresh the device list and try again.");

        var isTarget = await Task.Run(() => Device.IsInstallTarget(root), cancellationToken).ConfigureAwait(false);
        if (!isTarget)
            throw new InvalidOperationException("That folder no longer looks like a QuadStick (no default.csv at its root).");

        var result = await Task.Run(() =>
        {
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

    public Task<DeviceDeleteReceipt> DeleteAsync(
        DeviceProfileId profile,
        string recoveryDirectory,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_roots.TryGetValue(profile.Device.Value, out var root) || !Device.IsInstallTarget(root))
                throw new InvalidOperationException("That QuadStick is no longer available. Refresh the device list and try again.");

            var result = Device.DeleteProfile(root, profile.FileName, recoveryDirectory);
            return new DeviceDeleteReceipt(
                profile.FileName,
                new DeviceRecoveryReference(result.BackupPath));
        }, cancellationToken);

    string ResolveProfilePath(DeviceProfileId profile, bool requireInstallTarget)
    {
        if (!_roots.TryGetValue(profile.Device.Value, out var root))
            throw new InvalidOperationException("That QuadStick is no longer available. Refresh the device list and try again.");
        if (requireInstallTarget && !Device.IsInstallTarget(root))
            throw new InvalidOperationException("That QuadStick is no longer available. Refresh the device list and try again.");
        if (string.IsNullOrWhiteSpace(profile.FileName)
            || profile.FileName != Path.GetFileName(profile.FileName)
            || !DeviceProfileRules.IsProfileFileName(profile.FileName))
            throw new InvalidOperationException("Only a plain profile file name on the QuadStick can be accessed.");
        return Path.Combine(root, profile.FileName);
    }

    DeviceDescriptor Describe(string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var id = new DeviceId(OpaqueId(normalized));
        _roots[id.Value] = normalized;
        return new DeviceDescriptor(
            id,
            LabelFor(normalized),
            DeviceTransportKind.MountedVolume,
            DeviceCapabilities.ProfileStorage,
            normalized);
    }

    static string OpaqueId(string normalizedRoot)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot));
        return "mounted-volume:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string LabelFor(string root)
    {
        try
        {
            var match = DriveInfo.GetDrives().FirstOrDefault(d => string.Equals(
                Path.TrimEndingDirectorySeparator(d.RootDirectory.FullName),
                Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal));
            if (match is not null && !string.IsNullOrWhiteSpace(match.VolumeLabel))
                return match.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}