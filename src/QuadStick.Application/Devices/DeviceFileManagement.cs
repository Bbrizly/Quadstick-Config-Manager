using QuadStick.Format;

namespace QuadStick.Application.Devices;

public sealed record ManagedDeviceFile(
    DeviceProfileId Id,
    string Name,
    ProfileFile? Profile,
    string? ReadError,
    Exception? ParseFailure,
    bool Protected);

public sealed record ManagedDeviceGroup(
    DeviceDescriptor Device,
    IReadOnlyList<ManagedDeviceFile> Files,
    string? Error);

public enum LibraryCopyKind { NeedsReplaceConfirmation, RaceDetected, Copied }

public sealed record LibraryCopyResult(LibraryCopyKind Kind, string Destination);

/// <summary>Application workflow for profiles stored on a QuadStick. Device
/// identities are opaque; filesystem roots never cross this layer.</summary>
public sealed class DeviceFileManagementUseCase
{
    readonly IDeviceProfileStore _devices;
    readonly IDeviceDiscovery _discovery;
    readonly IProfileLibraryStore _library;

    public DeviceFileManagementUseCase(
        IDeviceProfileStore devices,
        IDeviceDiscovery discovery,
        IProfileLibraryStore library)
    {
        _devices = devices;
        _discovery = discovery;
        _library = library;
    }

    public void InvalidateDiscovery() => _discovery.InvalidateCache();

    public async Task<IReadOnlyList<ManagedDeviceGroup>> ListAsync(CancellationToken cancellationToken = default)
    {
        var source = await _devices.ListAsync(cancellationToken).ConfigureAwait(false);
        var groups = new List<ManagedDeviceGroup>(source.Count);
        foreach (var group in source)
        {
            var files = new List<ManagedDeviceFile>(group.Profiles.Count);
            foreach (var entry in group.Profiles)
            {
                ProfileFile? profile = null;
                Exception? parseFailure = null;
                string? readError = null;
                try
                {
                    var text = await _devices.ReadAsync(entry.Id, cancellationToken).ConfigureAwait(false);
                    profile = ProfileFile.Load(text);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or DirectoryNotFoundException or InvalidDataException
                                               or InvalidOperationException)
                {
                    readError = ex.Message;
                }
                catch (Exception ex)
                {
                    parseFailure = ex;
                }

                var protectedFile =
                    entry.FileName.Equals("default.csv", StringComparison.OrdinalIgnoreCase) ||
                    entry.FileName.Equals("prefs.csv", StringComparison.OrdinalIgnoreCase);
                files.Add(new ManagedDeviceFile(
                    entry.Id, entry.FileName, profile, readError, parseFailure, protectedFile));
            }
            groups.Add(new ManagedDeviceGroup(group.Device, files, group.Error));
        }
        return groups;
    }

    public async Task<ProfileFile> ReadProfileAsync(
        DeviceProfileId profile,
        CancellationToken cancellationToken = default) =>
        ProfileFile.Load(await _devices.ReadAsync(profile, cancellationToken).ConfigureAwait(false));

    public async Task<LibraryCopyResult> CopyToLibraryAsync(
        DeviceProfileId profile,
        string libraryDirectory,
        bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.Combine(libraryDirectory, profile.FileName);
        var existed = _library.Exists(destination);
        if (existed && !replaceExisting)
            return new LibraryCopyResult(LibraryCopyKind.NeedsReplaceConfirmation, destination);

        var text = await _devices.ReadAsync(profile, cancellationToken).ConfigureAwait(false);
        _library.EnsureDirectory(libraryDirectory);

        if (!existed && !replaceExisting)
        {
            if (!_library.TryCreate(destination, text))
                return new LibraryCopyResult(LibraryCopyKind.RaceDetected, destination);
        }
        else
        {
            _library.WriteAtomic(destination, text);
        }

        return new LibraryCopyResult(LibraryCopyKind.Copied, destination);
    }

    public Task<DeviceDeleteReceipt> DeleteAsync(
        DeviceProfileId profile,
        string recoveryDirectory,
        CancellationToken cancellationToken = default) =>
        _devices.DeleteAsync(profile, recoveryDirectory, cancellationToken);
}