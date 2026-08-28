using QuadStick.Format;

namespace QuadStick.App;

public sealed record DeviceFileSource(
    string Root,
    string Label,
    string Name,
    string Path,
    string? Text,
    string? ReadError);

public sealed record DeviceFileSourceGroup(
    string Root,
    string Label,
    IReadOnlyList<DeviceFileSource> Files,
    string? Error);

public interface IDeviceFileSource
{
    void InvalidateDiscovery();
    string DefaultBackupDirectory { get; }
    Task<IReadOnlyList<DeviceFileSourceGroup>> ListAsync(CancellationToken cancellationToken = default);
    Task<string> ReadAsync(string root, string fileName, CancellationToken cancellationToken = default);
    Task<DeviceDeleteReceipt> DeleteAsync(
        string root,
        string fileName,
        string backupDirectory,
        CancellationToken cancellationToken = default);
}

public sealed record DeviceDeleteReceipt(string DeletedPath, string BackupPath);

public interface IProfileSheetLinkResolver
{
    string? Resolve(ProfileFile profile);
}

public sealed record ManagedDeviceFile(
    string Root,
    string Label,
    string Name,
    string Path,
    ProfileFile? Profile,
    string? SheetUrl,
    string? ReadError,
    Exception? ParseFailure,
    bool Protected);

public sealed record ManagedDeviceGroup(
    string Root,
    string Label,
    IReadOnlyList<ManagedDeviceFile> Files,
    string? Error);

public enum LibraryCopyKind { NeedsReplaceConfirmation, RaceDetected, Copied }

public sealed record LibraryCopyResult(LibraryCopyKind Kind, string Destination);

/// <summary>Application workflow for mounted-device profile files. Presentation
/// decides how to ask confirmations; filesystem and mounted-volume mechanics
/// are behind ports.</summary>
public sealed class DeviceFileManagementUseCase
{
    readonly IDeviceFileSource _devices;
    readonly IProfileLibraryStore _library;
    readonly IProfileSheetLinkResolver _links;

    public DeviceFileManagementUseCase(
        IDeviceFileSource devices,
        IProfileLibraryStore library,
        IProfileSheetLinkResolver links)
    {
        _devices = devices;
        _library = library;
        _links = links;
    }

    public string DefaultBackupDirectory => _devices.DefaultBackupDirectory;

    public void InvalidateDiscovery() => _devices.InvalidateDiscovery();

    public async Task<IReadOnlyList<ManagedDeviceGroup>> ListAsync(CancellationToken cancellationToken = default)
    {
        var source = await _devices.ListAsync(cancellationToken).ConfigureAwait(false);
        var groups = new List<ManagedDeviceGroup>(source.Count);
        foreach (var group in source)
        {
            var files = new List<ManagedDeviceFile>(group.Files.Count);
            foreach (var file in group.Files)
            {
                ProfileFile? profile = null;
                Exception? parseFailure = null;
                string? sheetUrl = null;
                if (file.Text is not null)
                {
                    try
                    {
                        profile = ProfileFile.Load(file.Text);
                        sheetUrl = _links.Resolve(profile);
                    }
                    catch (Exception ex)
                    {
                        parseFailure = ex;
                    }
                }

                var protectedFile =
                    file.Name.Equals("default.csv", StringComparison.OrdinalIgnoreCase) ||
                    file.Name.Equals("prefs.csv", StringComparison.OrdinalIgnoreCase);
                files.Add(new ManagedDeviceFile(
                    file.Root, file.Label, file.Name, file.Path,
                    profile, sheetUrl, file.ReadError, parseFailure, protectedFile));
            }
            groups.Add(new ManagedDeviceGroup(group.Root, group.Label, files, group.Error));
        }
        return groups;
    }

    public async Task<ProfileFile> ReadProfileAsync(
        string root,
        string fileName,
        CancellationToken cancellationToken = default) =>
        ProfileFile.Load(await _devices.ReadAsync(root, fileName, cancellationToken).ConfigureAwait(false));

    public async Task<LibraryCopyResult> CopyToLibraryAsync(
        string root,
        string fileName,
        string libraryDirectory,
        bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.Combine(libraryDirectory, fileName);
        var existed = _library.Exists(destination);
        if (existed && !replaceExisting)
            return new LibraryCopyResult(LibraryCopyKind.NeedsReplaceConfirmation, destination);

        var text = await _devices.ReadAsync(root, fileName, cancellationToken).ConfigureAwait(false);
        _library.EnsureDirectory(libraryDirectory);

        // Existence checks are advisory only. The create-only publication is
        // the actual no-overwrite guarantee, so a file appearing between the
        // device read and the final rename can never be replaced accidentally.
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
        string root,
        string fileName,
        string backupDirectory,
        CancellationToken cancellationToken = default) =>
        _devices.DeleteAsync(root, fileName, backupDirectory, cancellationToken);
}
