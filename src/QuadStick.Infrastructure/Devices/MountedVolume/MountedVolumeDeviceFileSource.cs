using QuadStick.Format;

namespace QuadStick.App;

/// <summary>Mounted-volume implementation for listing/reading/deleting profile
/// files. Parsing and presentation are intentionally outside this adapter.</summary>
public sealed class MountedVolumeDeviceFileSource : IDeviceFileSource
{
    readonly Func<IReadOnlyList<string>> _findRoots;

    public MountedVolumeDeviceFileSource(Func<IReadOnlyList<string>>? findRoots = null) =>
        _findRoots = findRoots ?? (() => Device.FindCandidatesCached());

    public string DefaultBackupDirectory => Device.DefaultBackupDir();

    public void InvalidateDiscovery() => Device.InvalidateCandidateCache();

    public Task<IReadOnlyList<DeviceFileSourceGroup>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<DeviceFileSourceGroup>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groups = new List<DeviceFileSourceGroup>();
            foreach (var root in _findRoots())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] paths;
                try
                {
                    paths = Directory.GetFiles(root, "*.csv")
                        .Where(p => DeviceProfileRules.IsProfileFileName(Path.GetFileName(p)))
                        .ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
                {
                    groups.Add(new DeviceFileSourceGroup(root, LabelFor(root), Array.Empty<DeviceFileSource>(),
                        $"Could not read this drive: {ex.Message}"));
                    continue;
                }

                var files = new List<DeviceFileSource>();
                foreach (var path in paths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    string? text = null;
                    string? error = null;
                    try { text = File.ReadAllText(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
                    { error = ex.Message; }
                    files.Add(new DeviceFileSource(root, LabelFor(root), Path.GetFileName(path), path, text, error));
                }
                groups.Add(new DeviceFileSourceGroup(root, LabelFor(root), files, null));
            }
            return groups;
        }, cancellationToken);

    public Task<string> ReadAsync(string root, string fileName, CancellationToken cancellationToken = default) =>
        Task.Run(() => File.ReadAllText(RootFile(root, fileName)), cancellationToken);

    public Task<DeviceDeleteReceipt> DeleteAsync(
        string root,
        string fileName,
        string backupDirectory,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Device.DeleteProfile(root, fileName, backupDirectory);
            return new DeviceDeleteReceipt(result.DeletedPath, result.BackupPath);
        }, cancellationToken);

    static string RootFile(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName))
            throw new InvalidOperationException("Only a plain file name on the device root can be read.");
        return Path.Combine(root, fileName);
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

        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        return string.IsNullOrWhiteSpace(folder) ? root : folder;
    }
}

/// <summary>Provider-specific interpretation of a profile header's Google Sheet link.</summary>
public sealed class GoogleProfileSheetLinkResolver : IProfileSheetLinkResolver
{
    public string? Resolve(ProfileFile profile) =>
        SheetsUrl.TryGetEditUrlFromHeader(
            profile.Document.HeaderVersion,
            profile.Document.HeaderSource,
            out var url) ? url : null;
}
