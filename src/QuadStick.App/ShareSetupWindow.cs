using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace QuadStick.App;

// What to do when someone asks to share a profile and there is no Google
// connection yet. It used to open Settings with a one line status behind it,
// which told the user where to go and nothing about why or what came next.
//
// Three steps, in the order they have to happen: connect, save, then the thing
// they actually asked for. Each step says where it stands in words, never in a
// colour: a tick is not readable to a screen reader and not visible to a good
// number of the people this app is for.
public class ShareSetupWindow : Window
{
    readonly MainWindow _owner;
    readonly bool _needsSave;
    readonly string _finishLabel;

    readonly Step _connect;
    readonly Step _save;
    readonly Button _finish;
    readonly TextBlock _status;

    /// <summary>True when the user pressed the last step. The caller then runs
    /// the action they originally asked for, so the wizard never has a second
    /// copy of what share does.</summary>
    public bool Completed { get; private set; }

    public ShareSetupWindow(MainWindow owner, string finishLabel, bool needsSave)
    {
        Classes.Add("dialog");
        _owner = owner;
        _needsSave = needsSave;
        _finishLabel = finishLabel;

        Title = Strings.Share_SetUpGoogleSheets;
        Width = Math.Min(520 * owner.UiScale, 900);
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        int total = needsSave ? 3 : 2;

        var explain = new TextBlock
        {
            Text = Strings.Share_SharingAProfilePutsIt,
            FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };

        _connect = new Step(1, total, "Connect Google Drive", "Connect", ConnectAsync);
        _save = new Step(2, total, Strings.Share_SaveThisProfile, "Save", SaveStepAsync);

        _finish = new Button { Content = finishLabel, Classes = { "primary" }, MinWidth = 160 };
        _finish.Click += (_, _) => { Completed = true; Close(); };
        var finishStep = new Step(total, total, finishLabel, null, null, _finish);

        var cancel = new Button { Content = Strings.Share_Cancel, MinWidth = 140, IsCancel = true };
        AutomationProperties.SetName(cancel, Strings.Share_CloseWithoutSharing);
        cancel.Click += (_, _) => Close();

        _status = new TextBlock
        {
            Text = "", FontSize = Size("BodySize"), Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        var steps = new StackPanel { Spacing = 16 };
        steps.Children.Add(_connect.Panel);
        if (needsSave) steps.Children.Add(_save.Panel);
        steps.Children.Add(finishStep.Panel);

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 0 };
        panel.Children.Add(explain);
        panel.Children.Add(steps);
        panel.Children.Add(_status);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
            Children = { cancel },
        });

        Content = MainWindow.DialogShell(this, MainWindow.ZoomWrap(panel, owner.UiScale));
        // Before the window is up, not on Opened: the last step must never be
        // live for the frame between showing and the first refresh.
        Refresh();
        // Something focusable, always: a dialog whose only focus candidate is
        // disabled leaves the window with no focused element, and then Escape
        // reaches nothing and the window cannot be closed from the keyboard.
        Opened += (_, _) => (_connect.Action is { IsEnabled: true } first ? first : cancel).Focus();
    }

    // A fresh dialog may have no focused element, so handle Esc on the window.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    async Task ConnectAsync()
    {
        _status.Text = Strings.Share_WaitingForGoogleInYour;
        var ok = await _owner.ConnectGoogleAsync();
        _status.Text = ok ? "" : Strings.Share_CouldNotConnectToGoogle;
        Refresh();
    }

    async Task SaveStepAsync()
    {
        if (!await _owner.SaveProfileAsync())
            _status.Text = Strings.Share_ThisProfileWasNotSaved;
        Refresh();
    }

    // Every step's state, in words, every time anything changes.
    void Refresh()
    {
        bool connected = _owner.DriveConnected;
        bool saved = !_needsSave || _owner.ProfileIsSaved;

        _connect.SetDone(connected);
        _save.SetDone(_owner.ProfileIsSaved);
        _finish.IsEnabled = connected && saved;

        // Enabled is not a state a screen reader announces on its own, and it
        // is not a state a sighted user can explain either.
        AutomationProperties.SetName(_finish, _finish.IsEnabled
            ? _finishLabel
            : string.Format(CultureInfo.CurrentCulture, Strings.Share_FinishLabelNotAvailableYetFinish, _finishLabel));
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;

    // One row: what the step is, where it stands, and the button that does it.
    sealed class Step
    {
        public StackPanel Panel { get; }
        public Button? Action { get; }
        readonly TextBlock _state;
        readonly string _title;
        readonly int _number;
        readonly int _total;

        public Step(int number, int total, string title, string? actionLabel, Func<Task>? run,
            Button? existing = null)
        {
            _number = number;
            _total = total;
            _title = title;

            var label = new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, Strings.Share_StepNumberOfTotalTitle, number, total, title),
                FontSize = Size("BodySize"), FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
            };
            _state = new TextBlock
            {
                Text = Strings.Share_NotDoneYet, FontSize = Size("BodySize"), Classes = { "muted" },
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6),
            };

            Action = existing;
            if (existing is null && actionLabel is not null && run is not null)
            {
                Action = new Button { Content = actionLabel, MinWidth = 160 };
                Action.Click += async (_, _) => await run();
            }

            Panel = new StackPanel { Spacing = 0, Children = { label, _state } };
            if (Action is not null) Panel.Children.Add(Action);
            AutomationProperties.SetName(Panel, string.Format(CultureInfo.CurrentCulture, Strings.Share_StepNumberOfTotalTitle2, number, total, title));
        }

        public void SetDone(bool done)
        {
            _state.Text = done ? "Done" : Strings.Share_NotDoneYet2;
            if (Action is not null)
            {
                Action.IsEnabled = !done;
                AutomationProperties.SetName(Action,
                    done ? string.Format(CultureInfo.CurrentCulture, Strings.Share_TitleAlreadyDone, _title) : _title);
            }
            AutomationProperties.SetName(Panel,
                string.Format(CultureInfo.CurrentCulture,
                    done ? Strings.Share_StepDone : Strings.Share_StepNotDone, _number, _total, _title));
        }

        static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;
    }
}
