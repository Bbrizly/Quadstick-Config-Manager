using System.Net;
using QuadStick.Application.Backup;
using QuadStick.Format;

namespace QuadStick.Infrastructure.Google;

/// <summary>Maps concrete Google REST/auth failures to the provider-neutral
/// Application backup port without hiding cancellation or programming errors.</summary>
public sealed class GoogleDriveBackupProvider : IDriveBackupProvider
{
    readonly DriveClient _client;

    public GoogleDriveBackupProvider(DriveClient client) => _client = client;

    public Task<string> CreateSpreadsheetAsync(string title, CancellationToken cancellationToken = default) =>
        Translate(() => _client.CreateSpreadsheetAsync(title, cancellationToken));

    public Task DeleteSpreadsheetAsync(string id, CancellationToken cancellationToken = default) =>
        Translate(() => _client.DeleteSpreadsheetAsync(id, cancellationToken));

    public async Task<RemoteWriteReceipt> PushProfileAsync(
        string id,
        IReadOnlyList<ProfileTab> tabs,
        CancellationToken cancellationToken = default)
    {
        await Translate(() => _client.PushTabsAsync(id, tabs, cancellationToken)).ConfigureAwait(false);
        try
        {
            var revision = await Translate(() =>
                _client.GetModifiedTimeAsync(id, cancellationToken)).ConfigureAwait(false);
            return new RemoteWriteReceipt(true, revision);
        }
        catch (RemoteStorageException ex) when (ex.Kind is RemoteStorageFailureKind.Transient
                                                     or RemoteStorageFailureKind.RateLimited)
        {
            // Content is already on Google. Application records an explicit
            // unknown-revision state and replays local truth on the next pass.
            return new RemoteWriteReceipt(true, null);
        }
    }

    public Task<string> GetModifiedTimeAsync(string id, CancellationToken cancellationToken = default) =>
        Translate(() => _client.GetModifiedTimeAsync(id, cancellationToken));

    public Task<byte[]> DownloadWorkbookAsync(string id, CancellationToken cancellationToken = default) =>
        Translate(() => _client.DownloadWorkbookAsync(id, cancellationToken));

    public Task ShareAnyoneReaderAsync(string id, CancellationToken cancellationToken = default) =>
        Translate(() => _client.ShareAnyoneReaderAsync(id, cancellationToken));

    public async Task<IReadOnlyList<RemoteDriveSheet>> ListSpreadsheetsAsync(CancellationToken cancellationToken = default)
    {
        var sheets = await Translate(() => _client.ListSpreadsheetsAsync(cancellationToken)).ConfigureAwait(false);
        return sheets.Select(s => new RemoteDriveSheet(s.Id, s.Name, s.ModifiedTime)).ToList();
    }

    static async Task Translate(Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (GoogleAuthRevokedException ex) { throw MapRevoked(ex); }
        catch (DriveApiException ex) { throw MapDrive(ex); }
        catch (HttpRequestException ex) { throw MapTransient(ex); }
    }

    static async Task<T> Translate<T>(Func<Task<T>> operation)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (GoogleAuthRevokedException ex) { throw MapRevoked(ex); }
        catch (DriveApiException ex) { throw MapDrive(ex); }
        catch (HttpRequestException ex) { throw MapTransient(ex); }
    }

    static RemoteStorageException MapRevoked(GoogleAuthRevokedException ex) =>
        new(RemoteStorageFailureKind.AuthRevoked, ex.Message, ex);

    static RemoteStorageException MapDrive(DriveApiException ex) => ex.StatusCode switch
    {
        HttpStatusCode.NotFound =>
            new(RemoteStorageFailureKind.NotFound, ex.Message, ex),
        HttpStatusCode.Unauthorized =>
            new(RemoteStorageFailureKind.AuthRevoked, ex.Message, ex),
        HttpStatusCode.Forbidden =>
            new(RemoteStorageFailureKind.PermissionDenied, ex.Message, ex),
        HttpStatusCode.BadRequest =>
            new(RemoteStorageFailureKind.InvalidRequest, ex.Message, ex),
        HttpStatusCode.TooManyRequests =>
            new(RemoteStorageFailureKind.RateLimited, ex.Message, ex),
        >= HttpStatusCode.InternalServerError =>
            new(RemoteStorageFailureKind.Transient, ex.Message, ex),
        _ => new(RemoteStorageFailureKind.InvalidRequest, ex.Message, ex),
    };

    static RemoteStorageException MapTransient(HttpRequestException ex) =>
        new(RemoteStorageFailureKind.Transient, ex.Message, ex);
}