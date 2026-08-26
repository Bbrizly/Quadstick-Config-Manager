using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace QuadStick.App;

// The community list of shared game profiles. Same idiom as DrivePickerWindow:
// the list loads after the window opens, so home stays local and a network
// failure shows in this window instead of crashing.
//
// This window never writes to a QuadStick. A pick becomes a Google Sheets link
// and goes through MainWindow.ImportSheetsAsync, the one workbook import, which
// opens the profile in the editor. Installing still needs the editor, the
// validator, and the Install button, exactly as a hand pasted link does.
public class CommunityProfilesWindow : Window
{
    readonly MainWindow _owner;
    readonly CommunityCatalogClient _catalog;
    readonly HttpClient? _importHttp;

    readonly TextBox _search;
    readonly ListBox _list;
    readonly TextBlock _summary;
    readonly TextBlock _count;
    readonly TextBlock _status;
    readonly Button _import;
    readonly Button _openSheet;
    readonly Button _refresh;

    readonly List<CommunityProfile> _all = new();
    // Closing the window stops the fetch. The client has taken a token since it
    // was written and no caller ever gave it one, so a close left up to fifteen
    // seconds of request nobody wanted, finishing by writing the cache and
    // touching controls that were already gone.
    readonly CancellationTokenSource _closing = new();
    bool _closed;
    Task _loaded = Task.CompletedTask;

    /// <summary>The load started when the window opened. Tests await it; nothing
    /// in the app has to.</summary>
    internal Task CatalogLoaded => _loaded;

    /// <summary>How Open in Sheets reaches the browser. Tests swap it so a run
    /// never opens a real browser window.</summary>
    internal Func<Uri, Task> OpenUri { get; set; }

    public CommunityProfilesWindow(MainWindow owner) : this(owner, new CommunityCatalogClient(), null) { }

    /// <summary>Test seam: a catalog client with a fake handler and cache path,
    /// and the HttpClient the import should use.</summary>
    internal CommunityProfilesWindow(MainWindow owner, CommunityCatalogClient catalog, HttpClient? importHttp = null)
    {
        Classes.Add("dialog");
        _owner = owner;
        _catalog = catalog;
        _importHttp = importHttp;
        OpenUri = uri => Launcher.LaunchUriAsync(uri); // this window's own launcher
        Title = Strings.Community_CommunityProfiles;
        Width = Math.Min(640 * owner.UiScale, 1000);
        Height = Math.Min(600 * owner.UiScale, 800);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var explain = new TextBlock
        {
            Text = Strings.Community_GameProfilesOtherQuadStickPlayers,
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        };

        _search = new TextBox
        {
            Watermark = Strings.Community_SearchByGameFileName,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(_search, Strings.Community_SearchTheCommunityProfiles);
        _search.TextChanged += (_, _) => ApplyFilter();

        _summary = new TextBlock
        {
            Text = Strings.Community_LoadingTheCommunityList,
            FontSize = Size("BodySize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        };
        _count = new TextBlock
        {
            Text = "",
            FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
        };
        // What the load actually found goes here, and the loading line above it
        // is cleared on success, so without these the window went from
        // "Loading the community list..." to silence.
        AutomationProperties.SetLiveSetting(_summary, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_count, AutomationLiveSetting.Polite);

        // One list, so the keyboard reaches every result with the arrows
        // instead of hundreds of tab stops.
        _list = new ListBox { SelectionMode = SelectionMode.Single };
        AutomationProperties.SetName(_list, Strings.Community_CommunityProfilesUseTheArrow);
        _list.SelectionChanged += (_, _) => OnSelectionChanged();
        // Enter goes through the same gate the button does. ImportAsync turns
        // the button off while it runs, which stopped a second click but not a
        // second key press, so holding Enter through the download started two
        // imports and the later one replaced the earlier in the editor.
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; StartImport(); }
        };
        _search.KeyDown += (_, e) =>
        {
            // Down moves into the results without a Tab, Enter imports what is
            // picked, so the whole window works from the search box. A list is
            // not focusable itself; its rows are.
            if (e.Key == Key.Down && _list.ItemCount > 0) { FocusSelectedRow(); e.Handled = true; }
            else if (e.Key == Key.Enter) { e.Handled = true; StartImport(); }
        };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
        };

        _status = new TextBlock
        {
            Text = "",
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _import = new Button { Content = Strings.Community_Import, Classes = { "primary" }, MinWidth = 130, IsEnabled = false };
        AutomationProperties.SetName(_import, Strings.Community_ImportTheSelectedProfileInto);
        _import.Click += async (_, _) => await ImportAsync();

        _openSheet = new Button { Content = Strings.Community_OpenInSheets, MinWidth = 130, IsEnabled = false };
        AutomationProperties.SetName(_openSheet, Strings.Community_OpenTheSelectedProfileS);
        _openSheet.Click += async (_, _) => await OpenInSheetsAsync();

        _refresh = new Button { Content = Strings.Community_Refresh, MinWidth = 130 };
        AutomationProperties.SetName(_refresh, Strings.Community_DownloadTheCommunityListAgain);
        _refresh.Click += async (_, _) => await LoadAsync(refresh: true);

        var close = new Button { Content = Strings.Community_Close, MinWidth = 130, IsCancel = true };
        AutomationProperties.SetName(close, Strings.Community_CloseThisWindow);
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { _import, _openSheet, _refresh, close },
        };

        var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(24) };
        DockPanel.SetDock(explain, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(_summary, Dock.Top);
        DockPanel.SetDock(_count, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(buttons, Dock.Bottom);
        explain.Margin = new Thickness(0, 0, 0, 12);
        _search.Margin = new Thickness(0, 0, 0, 10);
        _summary.Margin = new Thickness(0, 0, 0, 4);
        _count.Margin = new Thickness(0, 0, 0, 10);
        _status.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(explain);
        panel.Children.Add(_search);
        panel.Children.Add(_summary);
        panel.Children.Add(_count);
        panel.Children.Add(_status);
        panel.Children.Add(buttons);
        panel.Children.Add(scroll);

        Content = MainWindow.DialogShell(this, MainWindow.ZoomWrap(panel, owner.UiScale));

        // Focus the search box so typing works from the first key press.
        Opened += (_, _) => _search.Focus();
        Opened += (_, _) => _loaded = LoadAsync();
    }

    // A fresh dialog may have no focused element, so handle Esc on the window.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Not disposed: the request in flight still holds this token, and the
        // source holds no timer or handle worth reclaiming by hand.
        _closed = true;
        _closing.Cancel();
        base.OnClosed(e);
    }

    /// <summary>The Google Sheets link for a profile. Built from the ID, never
    /// from a URL the catalog supplied, so a row cannot point the import
    /// somewhere else.</summary>
    internal static string EditUrl(CommunityProfile profile) =>
        $"https://docs.google.com/spreadsheets/d/{profile.SheetId}/edit";

    CommunityProfile? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as CommunityProfile;

    // The keyboard's way in to the import, past the same guard as the button.
    void StartImport()
    {
        if (_import.IsEnabled) _ = ImportAsync();
    }

    // Keyboard focus lands on a row, so the arrows walk the list from there.
    void FocusSelectedRow()
    {
        var index = Math.Max(_list.SelectedIndex, 0);
        _list.SelectedIndex = index;
        _list.ContainerFromIndex(index)?.Focus();
    }

    async Task LoadAsync(bool refresh = false)
    {
        _refresh.IsEnabled = false;
        _status.Text = refresh ? Strings.Community_CheckingQuadstickComForNew : Strings.Community_LoadingTheCommunityList2;
        try
        {
            var result = await _catalog.LoadAsync(refresh, _closing.Token);
            _all.Clear();
            _all.AddRange(result.Profiles);
            _summary.Text = Describe(result);
            // A refresh that quietly fell back to the saved copy used to clear
            // the status line, which is what a successful refresh does, so a
            // permanently broken endpoint looked exactly like a healthy offline
            // open. The user pressed a button and deserves to know it did not
            // reach quadstick.com.
            _status.Text = refresh && result.FromCache
                ? Strings.Community_CouldNotReachQuadstickCom
                : "";
        }
        // The window was closed while the request was in flight. Nothing to say
        // and nothing left to say it to.
        catch (OperationCanceledException) { return; }
        catch (CommunityCatalogException)
        {
            // Nothing downloaded and nothing saved. Say so plainly and leave
            // the window open: Refresh is right there, and the rest of the app
            // never needed this list.
            _all.Clear();
            _summary.Text = Strings.Community_TheCommunityListCouldNot;
            _status.Text = Strings.Community_CheckYourInternetConnectionAnd;
        }
        finally
        {
            if (!_closed)
            {
                _refresh.IsEnabled = true;
                ApplyFilter(keepStatus: true);
            }
        }
    }

    // Says where the rows came from. A saved copy is never described as new.
    static string Describe(CommunityCatalogResult result)
    {
        var count = result.Profiles.Count;
        string text;
        if (count == 0)
            text = result.FromCache
                ? Strings.Community_TheSavedCopyOfThe
                : Strings.Community_TheCommunityListHasNo;
        else
            text = result.FromCache
                ? string.Format(CultureInfo.CurrentCulture, Strings.Community_ProfilesCountFromTheCopy, Profiles(count))
                : string.Format(CultureInfo.CurrentCulture, Strings.Community_ProfilesCountDownloadedJustNow, Profiles(count));

        if (result.SkippedRows > 0)
            text += " " + Plural.Of(result.SkippedRows, "Community_SkippedRow");
        return text;
    }

    static string Profiles(int count) => count == 1 ? "1 profile" : $"{count} profiles";

    void ApplyFilter(bool keepStatus = false)
    {
        var query = (_search.Text ?? "").Trim();
        var matches = _all.Where(p => Matches(p, query)).ToList();
        var rows = matches.Select(MakeRow).ToList();

        _list.ItemsSource = rows;
        // Picking the first match keeps Import one key away after a search.
        _list.SelectedIndex = rows.Count > 0 ? 0 : -1;

        if (_all.Count == 0) _count.Text = "";
        else if (query.Length == 0) _count.Text = string.Format(CultureInfo.CurrentCulture, Strings.Community_ShowingAllProfilesAllCount, Profiles(_all.Count));
        else if (matches.Count == 0) _count.Text = string.Format(CultureInfo.CurrentCulture, Strings.Community_NoProfilesMatchQuery, query);
        else _count.Text = string.Format(CultureInfo.CurrentCulture, Strings.Community_ShowingMatchesCountOfAll, matches.Count, _all.Count, query);

        if (!keepStatus && _all.Count > 0 && matches.Count == 0)
            _status.Text = Strings.Community_NothingMatchesThatSearchClear;
    }

    static bool Matches(CommunityProfile p, string query)
    {
        if (query.Length == 0) return true;
        return Has(p.Name) || Has(p.CsvName) || Has(p.Connection) || Has(p.Pointer);
        bool Has(string field) => field.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    // One row per profile. Optional fields are shown only when the catalog
    // filled them in, and the same words go to a screen reader.
    static ListBoxItem MakeRow(CommunityProfile p)
    {
        var details = new[] { p.CsvName, p.Connection, p.Pointer, p.Notes }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = p.Name,
            FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap,
        });
        if (details.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", details),
                FontSize = Size("SmallSize"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
            });

        var row = new ListBoxItem { Content = stack, Tag = p };
        AutomationProperties.SetName(row,
            details.Length > 0 ? $"{p.Name}. {string.Join(", ", details)}" : p.Name);
        return row;
    }

    // Selection is announced in words, so it does not depend on seeing the
    // highlight colour.
    void OnSelectionChanged()
    {
        var picked = Selected;
        _import.IsEnabled = picked is not null;
        _openSheet.IsEnabled = picked is not null;
        if (picked is not null) _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Community_PickedNameIsSelected, picked.Name);
    }

    async Task ImportAsync()
    {
        if (Selected is not { } picked) { _status.Text = Strings.Community_PickAProfileFromThe; return; }

        _import.IsEnabled = false;
        _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Community_ImportingPickedName, picked.Name);
        string? failure = null;
        try
        {
            // The one workbook import. Its errors normally land on the home
            // screen next to the paste box, which nobody would see from here,
            // so they come back through the callback and land on this window's
            // own status line instead.
            // The import review opens over THIS window, not over MainWindow:
            // this one is modal, so a dialog owned by the main window would
            // open behind a window the user cannot click.
            await _owner.ImportSheetsAsync(EditUrl(picked), _importHttp, message => failure = message, this);
        }
        catch (Exception ex)
        {
            failure = string.Format(CultureInfo.CurrentCulture, Strings.Community_CouldNotImportPickedName, picked.Name, ex.Message);
        }
        finally
        {
            _import.IsEnabled = Selected is not null;
        }

        if (failure is not null) { _status.Text = failure; return; }
        // The profile is open in the editor behind this window, so get out of
        // the way rather than hiding the thing the user just asked for.
        Close();
    }

    async Task OpenInSheetsAsync()
    {
        if (Selected is not { } picked) { _status.Text = Strings.Community_PickAProfileFromThe2; return; }
        try
        {
            await OpenUri(new Uri(EditUrl(picked)));
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Community_OpenedThePickedNameSheet, picked.Name);
        }
        catch (Exception ex)
        {
            _status.Text = string.Format(CultureInfo.CurrentCulture, Strings.Community_CouldNotOpenTheSheet, ex.Message);
        }
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;
}
