using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using QuadStick.Format;

namespace QuadStick.App;

// The editor's half of a clinician session: which profile is served, and how a
// proposal becomes a saved file. RemoteSession owns the wire; nothing in here
// knows there is a socket. Everything that writes is behind the patient's click.
public partial class MainWindow
{
    RemoteSessionWindow? _remoteWindow;
    int _installs; // bumped by a finished install, so a session can say whether one happened

    /// <summary>Open the session window, or bring the open one forward.</summary>
    internal void ShowRemoteSession()
    {
        if (!NetworkFeature.Enabled) return;
        if (_remoteWindow is { } open) { open.Activate(); return; }
        _remoteWindow = new RemoteSessionWindow(this, RemoteSession.RelayBase(_settings.RelayUrl));
        _remoteWindow.Closed += (_, _) => _remoteWindow = null;
        _remoteWindow.Show(this);
    }

    internal RemoteSessionWindow? RemoteWindowForTest => _remoteWindow;

    MenuItem RemoteMenuItem()
    {
        var item = new MenuItem { Header = Strings.Remote_ShareWithClinician };
        AutomationProperties.SetName(item, Strings.Remote_ShareWithClinicianName);
        item.Click += (_, _) => ShowRemoteSession();
        return item;
    }

    /// <summary>The sheet the editor lights rows on, which is what live frames
    /// light too. Null with no profile open.</summary>
    internal ModeSheet? RemoteLiveSheet => _file is null ? null : CurrentSheet;

    /// <summary>The practice-light rule, one copy for the editor and the
    /// session: a row is firing when the device is sending its output.</summary>
    internal static IEnumerable<Binding> FiringBindings(LiveState? state, ModeSheet? sheet) =>
        state is { OutputsUnderstood: true } && sheet is not null
            ? sheet.Bindings.Where(b => state.Outputs.Contains(b.Output))
            : Enumerable.Empty<Binding>();

    /// <summary>What a clinician gets on profile.get: the open profile's SAVED
    /// text, never the edits on screen. With nothing open, the device's own
    /// default.csv if a QuadStick drive is mounted.</summary>
    internal (RemoteProfile? Profile, string? Error) RemoteCurrentProfile()
    {
        if (_file is not null)
        {
            if (_file.Dirty || _savePath is null) return (null, "unsaved");
            return Read(_savePath, fromDevice: IsOnDevice(_savePath));
        }
        foreach (var root in FindDeviceRoots())
        {
            var path = Path.Combine(root, "default.csv");
            if (File.Exists(path)) return Read(path, fromDevice: true);
        }
        return (null, "none");

        static (RemoteProfile?, string?) Read(string path, bool fromDevice)
        {
            try
            {
                var csv = File.ReadAllText(path);
                return (new RemoteProfile(Path.GetFileName(path), csv, RemoteCrypto.Rev(csv), path, fromDevice), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, "none"); }
        }
    }

    static bool IsOnDevice(string path) =>
        Path.GetDirectoryName(path) is string dir && Device.IsInstallTarget(dir);

    /// <summary>The clinician asked while there are unsaved edits. Said on the
    /// status line rather than in a dialog, so it never stacks over the work.</summary>
    internal void RemoteAskToSave() => Status(Strings.Remote_SaveFirst, StatusKind.Warning);

    /// <summary>Shows the proposal and returns true only for Use this. Tests
    /// swap it, the way ShowImportReview is swapped.</summary>
    internal Func<RemoteProposal, Task<bool>>? AskRemoteProposalOverride;
    internal Task<bool> AskRemoteProposal(RemoteProposal p) => (AskRemoteProposalOverride ?? ShowRemoteProposalAsync)(p);

    /// <summary>Asked after a proposal is saved. True when an install finished.</summary>
    internal Func<Task<bool>>? OfferRemoteInstallOverride;

    async Task<bool> OfferRemoteInstall()
    {
        if (OfferRemoteInstallOverride is { } over) return await over();
        if (!await ConfirmAsync(Strings.Remote_InstallTitle, Strings.Remote_InstallBody)) return false;
        int before = _installs;
        await RunInstallFlowAsync(); // the existing flow: backup, verify, swap
        return _installs > before;
    }

    internal void CountInstall() => _installs++;

    /// <summary>Use this: keep a rescue copy of what is there, write the
    /// clinician's file, open it, and offer the install. A file served off the
    /// QuadStick is saved into the library instead, because only Install writes
    /// to the device. The clinician's name for the file is never a path.</summary>
    internal async Task<(string? Code, string? Rev, bool Installed)> SaveRemoteProposalAsync(RemoteProfile was, string csv)
    {
        // The dialog was open for a while; the file may have moved under it.
        var (now, _) = RemoteCurrentProfile();
        if (now is null || now.Rev != was.Rev || now.Path != was.Path) return ("stale", null, false);

        var target = was.FromDevice ? Path.Combine(LibraryDir, was.Name) : was.Path;
        bool stale = false;
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) { ProfileFile.WriteAtomic(target, csv); return; }
                // Read once, right before the write: the text checked is the
                // text rescued, so nothing saved in between is lost unseen.
                var old = File.ReadAllText(target);
                if (!was.FromDevice && RemoteCrypto.Rev(old) != was.Rev) { stale = true; return; }
                Directory.CreateDirectory(CrashGuard.RescueDir);
                var name = Path.GetFileNameWithoutExtension(target);
                File.WriteAllText(Path.Combine(CrashGuard.RescueDir,
                    $"{name}-before-clinician-{DateTime.Now:yyyyMMdd-HHmmss}-{DateTime.Now.Ticks % 10000}.csv"), old);
                ProfileFile.WriteAtomic(target, csv);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status(string.Format(CultureInfo.CurrentCulture, Strings.Main_CouldNotSaveExMessage, ex.Message), StatusKind.Error);
            return ("save_failed", null, false);
        }
        if (stale) return ("stale", null, false);

        OpenInEditor(ProfileFile.Load(csv), target, ProfileSource.File);
        Status(string.Format(CultureInfo.CurrentCulture, Strings.Remote_Saved, target), StatusKind.Ready);
        var installed = await OfferRemoteInstall();
        return (null, RemoteCrypto.Rev(csv), installed);
    }

    // ponytail: a minimal change list. Swap to the follow-link diff dialog
    // (ProfileChanges.cs on branch follow-link) once that is merged to main.
    async Task<bool> ShowRemoteProposalAsync(RemoteProposal p)
    {
        var keep = new Button { Content = Strings.Remote_Keep, MinWidth = 140, IsDefault = true, IsCancel = true };
        var use = new Button { Content = Strings.Remote_UseThis, MinWidth = 140, Classes = { "primary" } };
        AutomationProperties.SetName(keep, Strings.Remote_KeepName);
        AutomationProperties.SetName(use, Strings.Remote_UseThisName);

        var changes = new StackPanel { Spacing = 4 };
        foreach (var line in RemoteChanges(p.Current.Csv, p.Csv))
            changes.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") });

        var body = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 14, MaxWidth = 560,
            Children =
            {
                new TextBlock { Text = Strings.Remote_ProposalTitle, FontWeight = FontWeight.Bold,
                                FontSize = Size("SubheadSize"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, Strings.Remote_ProposalFor, p.Current.Name),
                                TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize") },
            },
        };
        if (p.Note.Trim().Length > 0)
            body.Children.Add(new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, Strings.Remote_ProposalNote, p.Note.Trim()),
                TextWrapping = TextWrapping.Wrap, FontSize = Size("BodySize"),
            });
        var scroll = new ScrollViewer { Content = changes, MaxHeight = 360 };
        AutomationProperties.SetName(scroll, Strings.Remote_ChangesName);
        body.Children.Add(scroll);
        body.Children.Add(new TextBlock { Text = Strings.Remote_ProposalSafe, TextWrapping = TextWrapping.Wrap,
                                          FontSize = Size("BodySize"), Classes = { "muted" } });
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { keep, use } });

        var dialog = new Window
        {
            Title = Strings.Remote_ProposalTitle,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = ZoomWrap(body, _uiScale),
        };
        var result = false;
        keep.Click += (_, _) => dialog.Close();
        use.Click += (_, _) => { result = true; dialog.Close(); };
        dialog.Opened += (_, _) => keep.Focus(); // Keep is the default, and where the keyboard starts
        await ShowDialogInShellAsync(dialog);
        return result;
    }

    /// <summary>Which modes and rows the proposal changes, one line each.
    /// Modes are matched by position, the way the firmware counts them. Every
    /// changed line of the file is listed, bindings under their mode and the
    /// rest (names, settings, comments) after, so nothing is accepted unseen.</summary>
    internal static List<string> RemoteChanges(string oldCsv, string newCsv)
    {
        var lines = new List<string>();
        if (oldCsv == newCsv) { lines.Add(Strings.Remote_NoChanges); return lines; }
        ProfileFile a, b;
        try { a = ProfileFile.Load(oldCsv); }
        catch (Exception) { a = ProfileFile.Load(""); }
        b = ProfileFile.Load(newCsv);

        int mode = 0;
        var sa = a.Document.Sheets;
        var sb = b.Document.Sheets;
        for (int i = 0; i < Math.Max(sa.Count, sb.Count); i++)
        {
            var sheet = i < sb.Count ? sb[i] : sa[i];
            var label = sheet.Type == SheetType.ProfileName
                ? string.Format(CultureInfo.CurrentCulture, Strings.Remote_ModeLabel, ++mode, sheet.ModeName)
                : sheet.Type.ToString();
            if (i >= sa.Count) { lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Remote_ModeAdded, label)); continue; }
            if (i >= sb.Count) { lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Remote_ModeRemoved, label)); continue; }
            AddDiff(lines, label, sa[i].Bindings.Select(x => RowText(a, x.Row)), sb[i].Bindings.Select(x => RowText(b, x.Row)));
        }
        AddDiff(lines, Strings.Remote_OtherLines, OtherRows(a), OtherRows(b));
        if (lines.Count == 0) lines.Add(Strings.Remote_OtherChanges);
        return lines;
    }

    static void AddDiff(List<string> lines, string heading, IEnumerable<string> before, IEnumerable<string> after)
    {
        var removed = before.ToList();
        var added = new List<string>();
        foreach (var row in after)
            if (!removed.Remove(row)) added.Add(row);
        if (removed.Count == 0 && added.Count == 0) return;
        lines.Add(heading);
        lines.AddRange(removed.Select(r => string.Format(CultureInfo.CurrentCulture, Strings.Remote_RowRemoved, r)));
        lines.AddRange(added.Select(r => string.Format(CultureInfo.CurrentCulture, Strings.Remote_RowAdded, r)));
    }

    // A whole row as the file holds it, every cell, trailing blanks dropped.
    static string RowText(ProfileFile f, int row) =>
        row - 1 < f.Grid.Count ? string.Join(", ", f.Grid[row - 1].Reverse().SkipWhile(c => c.Trim().Length == 0).Reverse()) : "";

    // Every non-blank row that is not a binding: headers, mode names, settings
    // the parser does not read as a binding, comments.
    static IEnumerable<string> OtherRows(ProfileFile f)
    {
        var bound = f.Document.Sheets.SelectMany(x => x.Bindings).Select(x => x.Row).ToHashSet();
        for (int r = 1; r <= f.Grid.Count; r++)
            if (!bound.Contains(r) && RowText(f, r) is { Length: > 0 } t) yield return t;
    }
}
