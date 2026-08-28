using System.Net;

namespace QuadStick.App;

/// <summary>Maps the concrete Google REST client and authentication failures to
/// the provider-neutral Application backup port.</summary>
public sealed class GoogleDriveBackupProvider : IDriveBackupProvider
{
    readonly DriveClient _client;

    public GoogleDriveBackupProvider(DriveClient client) => _client = client;

    public Task<string> CreateSpreadsheetAsync(string title, CancellationToken cancellationToken = default) =>
        Translate(() => _client.CreateSpreadsheetAsync(title, cancellationToken));

    public Task PushTabsAsync(string id, IReadOnlyList<QuadStick.Format.ProfileTab> tabs, CancellationToken cancellationToken = default) =>
        Translate(() => _client.PushTabsAsync(id, tabs, cancellationToken));

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

    static RemoteStorageException MapDrive(DriveApiException ex) =>
        ex.StatusCode == HttpStatusCode.NotFound
            ? new RemoteStorageException(RemoteStorageFailureKind.NotFound, ex.Message, ex)
            : new RemoteStorageException(RemoteStorageFailureKind.Transient, ex.Message, ex);

    static RemoteStorageException MapTransient(HttpRequestException ex) =>
        new(RemoteStorageFailureKind.Transient, ex.Message, ex);
}
