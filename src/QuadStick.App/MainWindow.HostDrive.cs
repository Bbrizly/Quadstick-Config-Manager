using System.Globalization;

namespace QuadStick.App;

/// <summary>What a host's share came to: the link, or null and why not.</summary>
public sealed record HostShareResult(string? Url, string Message);

// Sharing for a host that keeps the network off and allows Google Drive alone
// (NetworkFeature.GoogleDriveOnly). The host has its own button and its own
// warning; this is the same five steps "Copy share link" runs, minus the
// setup wizard and the clipboard, which are the host's to word.
public partial class MainWindow
{
    /// <summary>Put one saved profile in a Google Sheet, turn on "anyone with
    /// the link can view", and hand back the link. The host asks first: this
    /// uploads without another question, because the free app's own question
    /// comes after the upload and a no to it would not take the upload back. Asks the user to sign in
    /// when no Google account is connected yet. After this, every save of that
    /// file pushes to the same sheet, and a sheet edited online is asked about
    /// on the next save, exactly as in the free app.</summary>
    public async Task<HostShareResult> ShareForHostAsync(string path)
    {
        if (!NetworkFeature.DriveAllowed || !GoogleAuth.IsConfigured)
            return new HostShareResult(null, Strings.Main_GoogleDriveBackupIsNot);

        // The sheet gets what is on disk. Unsaved work in the editor would be
        // left out of it without a word, so it is saved first.
        if (_savePath == path && _file is { Dirty: true } && !await SaveAsync())
            return new HostShareResult(null, Strings.Main_SaveThisProfileFirstThen);

        if (Backup() is null && !await ConnectGoogleAsync())
            return new HostShareResult(null, Strings.Share_CouldNotConnectToGoogle);
        if (Backup() is not DriveBackup backup)
            return new HostShareResult(null, Strings.Share_CouldNotConnectToGoogle);

        string csv;
        try { csv = await File.ReadAllTextAsync(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HostShareResult(null, string.Format(CultureInfo.CurrentCulture,
                Strings.Main_CouldNotReadBareNamePath, BareName(Path.GetFileName(path)), ex.Message));
        }

        if (_backupInFlight is Task inFlight)
            try { await inFlight; } catch { /* RunBackup already reported it */ }

        ShareLinkResult result;
        try { result = await backup.GetShareLinkAsync(path, csv, askedAlready: true); }
        catch (Exception ex)
        {
            return new HostShareResult(null, string.Format(CultureInfo.CurrentCulture,
                Strings.Main_CouldNotShareThisProfile, ex.Message));
        }
        if (result.DownloadedCsv is string kept) ApplyKeptOnline(path, kept);
        return result.Kind is ShareLinkKind.Copied or ShareLinkKind.CopiedStale
            ? new HostShareResult(result.Url, result.Message)
            : new HostShareResult(null, result.Message);
    }

    /// <summary>Forget the sheet a profile pushes to, for a host deleting the
    /// person it belonged to. The sheet itself stays in the Drive it is in:
    /// this app's Google access cannot be trusted to delete what a person may
    /// still be using, and the host says so.</summary>
    public void ForgetDriveLink(string path)
    {
        if (_settings.DriveLinks.Remove(path)) PersistSettings();
    }

    /// <summary>The same, with no editor open: straight to the settings file.</summary>
    public static void ForgetDriveLinkInSettings(string path)
    {
        var s = Settings.Load();
        if (s.DriveLinks.Remove(path)) Settings.Save(s);
    }
}
