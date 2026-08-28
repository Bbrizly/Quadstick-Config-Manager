using QuadStick.Application.Backup;
using QuadStick.Application.Profiles;
using QuadStick.Application.Settings;
using Xunit;

namespace QuadStick.Format.Tests;

/// <summary>State-safety tests for the provider-neutral backup workflow. These
/// deliberately use handwritten ports so a persistence failure cannot be hidden
/// by the Google adapter or the presentation compatibility facade.</summary>
public sealed class DriveWorkflowSafetyTests
{
    static string ValidProfile => ProfileFile.NewFromTemplate("safe.csv").ToCsvText();

    [Fact]
    public async Task ExistingBackup_DoesNotTouchRemote_WhenPendingMarkerCannotPersist()
    {
        var links = new FakeLinkStore
        {
            Link = new DriveLink
            {
                SpreadsheetId = "sheet-1",
                LastSeenModifiedTime = "rev-1",
                BackupDirty = false,
                RevisionState = RemoteRevisionState.Known,
            },
            FailSet = true,
        };
        var provider = new RecordingProvider();
        var workflow = new DriveBackupWorkflow(provider, links, new FakeLibraryStore());

        var result = await workflow.PushAsync("/profiles/safe.csv", ValidProfile);

        Assert.Equal(BackupPushState.Failed, result.State);
        Assert.True(result.SettingsSaveFailed);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task MissingRemote_DoesNotReportBackupOff_WhenLinkRemovalCannotPersist()
    {
        var links = new FakeLinkStore
        {
            Link = new DriveLink
            {
                SpreadsheetId = "missing-sheet",
                LastSeenModifiedTime = "rev-1",
                BackupDirty = false,
                RevisionState = RemoteRevisionState.Known,
            },
            FailRemove = true,
        };
        var provider = new RecordingProvider { ModifiedFailure = RemoteStorageFailureKind.NotFound };
        var workflow = new DriveBackupWorkflow(provider, links, new FakeLibraryStore());

        var result = await workflow.PushAsync(
            "/profiles/safe.csv", ValidProfile, recreateMissing: false);

        Assert.Equal(BackupPushState.Failed, result.State);
        Assert.True(result.SettingsSaveFailed);
        Assert.NotNull(links.Link);
    }

    [Fact]
    public void DriveLinkStore_TryMove_ReturnsFalse_WhenNothingMoved()
    {
        var store = new MemorySettingsStore(new AppSettings());
        var links = new DriveLinkStore(new AppSettingsSession(store));

        var moved = links.TryMove("/missing.csv", "/new.csv");

        Assert.False(moved);
        Assert.Empty(links.Snapshot());
    }

    sealed class RecordingProvider : IDriveBackupProvider
    {
        public int Calls { get; private set; }
        public RemoteStorageFailureKind? ModifiedFailure { get; init; }

        public Task<string> CreateSpreadsheetAsync(string title, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult("created"); }

        public Task DeleteSpreadsheetAsync(string id, CancellationToken cancellationToken = default)
        { Calls++; return Task.CompletedTask; }

        public Task<RemoteWriteReceipt> PushProfileAsync(
            string id, IReadOnlyList<ProfileTab> tabs, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new RemoteWriteReceipt(true, "rev-2")); }

        public Task<string> GetModifiedTimeAsync(string id, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (ModifiedFailure is { } failure)
                throw new RemoteStorageException(failure, "test failure");
            return Task.FromResult("rev-1");
        }

        public Task<byte[]> DownloadWorkbookAsync(string id, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(Array.Empty<byte>()); }

        public Task ShareAnyoneReaderAsync(string id, CancellationToken cancellationToken = default)
        { Calls++; return Task.CompletedTask; }

        public Task<IReadOnlyList<RemoteDriveSheet>> ListSpreadsheetsAsync(CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<IReadOnlyList<RemoteDriveSheet>>(Array.Empty<RemoteDriveSheet>()); }
    }

    sealed class FakeLinkStore : IDriveLinkStore
    {
        public DriveLink? Link { get; set; }
        public bool FailSet { get; init; }
        public bool FailRemove { get; init; }

        public DriveLink? Get(string profilePath) => Link?.DeepClone();

        public IReadOnlyDictionary<string, DriveLink> Snapshot() => Link is null
            ? new Dictionary<string, DriveLink>()
            : new Dictionary<string, DriveLink> { ["/profiles/safe.csv"] = Link.DeepClone() };

        public bool TrySet(string profilePath, DriveLink link)
        {
            if (FailSet) return false;
            Link = link.DeepClone();
            return true;
        }

        public bool TryRemove(string profilePath)
        {
            if (FailRemove) return false;
            Link = null;
            return true;
        }

        public bool TryMove(string oldPath, string newPath) => false;
    }

    sealed class FakeLibraryStore : IProfileLibraryStore
    {
        public bool Exists(string path) => false;
        public IReadOnlyList<string> ListCsvFiles(string directory) => Array.Empty<string>();
        public void EnsureDirectory(string directory) { }
        public string ReadText(string path) => throw new FileNotFoundException();
        public void WriteAtomic(string path, string text) { }
        public bool TryCreate(string path, string text) => true;
        public void Delete(string path) { }
    }

    sealed class MemorySettingsStore : IAppSettingsStore
    {
        AppSettings _settings;
        public MemorySettingsStore(AppSettings settings) => _settings = settings.DeepClone();
        public AppSettingsLoadResult Load() => new(_settings.DeepClone());
        public bool TrySave(AppSettings settings)
        {
            _settings = settings.DeepClone();
            return true;
        }
    }
}
