using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace QuadStick.App;

// Share with my clinician. Start makes a code, the patient reads it out or
// sends the link, and the window says in words where the session stands. Not
// modal: the patient keeps working in the editor while the clinician watches.
// Closing it ends the session.
public class RemoteSessionWindow : Window
{
    readonly MainWindow _owner;
    readonly Uri _relay;
    RemoteSession? _session;

    internal Func<string, IRemoteSocket> NewSocket = _ => new WebSocketTransport();

    readonly Button _start;
    readonly Button _end;
    readonly Button _copyCode;
    readonly Button _copyLink;
    readonly TextBlock _code;
    readonly StackPanel _codePanel;
    readonly TextBlock _status;
    readonly TextBlock _notice;
    readonly Border _watching;

    internal RemoteSession? Session => _session;
    internal Button StartButton => _start;
    internal Button EndButton => _end;
    internal string StatusText => _status.Text ?? "";
    internal bool WatchingShown => _watching.IsVisible;

    public RemoteSessionWindow(MainWindow owner, Uri relay)
    {
        Classes.Add("dialog");
        _owner = owner;
        _relay = relay;
        Title = Strings.Remote_WindowTitle;
        Width = Math.Min(560 * owner.UiScale, 900);
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var explain = new TextBlock
        {
            Text = Strings.Remote_Explain, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap,
        };

        _start = new Button { Content = Strings.Remote_Start, Classes = { "primary" }, MinWidth = 160 };
        AutomationProperties.SetName(_start, Strings.Remote_StartName);
        _start.Click += (_, _) => Start();

        _code = new TextBlock
        {
            FontSize = Size("SubheadSize") * 2.2, FontWeight = FontWeight.Bold,
            FontFamily = new FontFamily("Menlo,Consolas,monospace"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8),
        };
        _copyCode = new Button { Content = Strings.Remote_CopyCode, MinWidth = 140 };
        _copyLink = new Button { Content = Strings.Remote_CopyLink, MinWidth = 140 };
        AutomationProperties.SetName(_copyCode, Strings.Remote_CopyCodeName);
        AutomationProperties.SetName(_copyLink, Strings.Remote_CopyLinkName);
        _copyCode.Click += async (_, _) => await CopyAsync(RemoteCrypto.Display(_session!.Code));
        _copyLink.Click += async (_, _) => await CopyAsync(RemoteSession.JoinLink(_session!.Code));
        _codePanel = new StackPanel
        {
            Spacing = 8, IsVisible = false,
            Children =
            {
                new TextBlock { Text = Strings.Remote_YourCode, FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap },
                _code,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _copyCode, _copyLink } },
            },
        };

        _status = new TextBlock { FontSize = Size("BodySize"), FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _notice = new TextBlock { FontSize = Size("BodySize"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite);

        // Words, not a coloured dot: being watched has to be readable by
        // somebody who cannot tell the colours apart, and spoken aloud.
        var watchingText = new TextBlock
        {
            Text = Strings.Remote_Watching, FontSize = Size("BodySize"), FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetLiveSetting(watchingText, AutomationLiveSetting.Assertive);
        _watching = new Border
        {
            Child = watchingText, Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2), IsVisible = false,
        };
        MainWindow.BindBrushTo(_watching, Border.BorderBrushProperty, "Accent");

        _end = new Button { Content = Strings.Remote_End, MinWidth = 140, IsVisible = false };
        AutomationProperties.SetName(_end, Strings.Remote_EndName);
        _end.Click += async (_, _) => { if (_session is { } s) await s.EndAsync(); };
        var close = new Button { Content = Strings.Remote_Close, MinWidth = 140, IsCancel = true };
        close.Click += (_, _) => Close();

        var panel = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                explain, _start, _codePanel, _status, _notice, _watching,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { _end, close },
                },
            },
        };
        Content = MainWindow.DialogShell(this, MainWindow.ZoomWrap(panel, owner.UiScale));
        Opened += (_, _) => _start.Focus();
        Closed += async (_, _) => { if (_session is { } s) await s.EndAsync(); };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    /// <summary>A new code and a new session. Called again after one ends.</summary>
    internal void Start()
    {
        var code = RemoteCrypto.NewCode();
        var session = new RemoteSession(_owner, NewSocket(code), code);
        _session = session;
        session.Changed += () => { if (_session == session) Refresh(); };
        session.Notice += text => { if (_session == session) { _notice.Text = text; _notice.IsVisible = true; } };
        _code.Text = RemoteCrypto.Display(code);
        // Spelled out, so a screen reader reads K 7 Q M and not a word.
        AutomationProperties.SetName(_code, string.Format(CultureInfo.CurrentCulture, Strings.Remote_CodeName,
            string.Join(" ", code.ToCharArray())));
        _notice.IsVisible = false;
        Refresh();
        _ = session.RunAsync(_relay);
    }

    void Refresh()
    {
        var s = _session;
        bool running = s is { Status: not RemoteStatus.Ended };
        _start.IsVisible = !running;
        _start.Content = s is null ? Strings.Remote_Start : Strings.Remote_StartAgain;
        _codePanel.IsVisible = running;
        _end.IsVisible = running;
        _watching.IsVisible = s is { LiveOn: true, Status: RemoteStatus.Connected };
        _status.Text = s?.Status switch
        {
            RemoteStatus.Connecting => Strings.Remote_Connecting,
            RemoteStatus.Waiting => Strings.Remote_Waiting,
            RemoteStatus.Connected => Strings.Remote_Connected,
            RemoteStatus.Ended => s.EndReason ?? Strings.Remote_Ended,
            _ => "",
        };
        if (s is { Status: RemoteStatus.Ended }) _notice.IsVisible = false;
        // Focus never left on a button that just disappeared.
        if (!running && _end.IsFocused) _start.Focus();
        if (running && _start.IsFocused) _copyCode.Focus();
    }

    async Task CopyAsync(string text)
    {
        if (Clipboard is { } c) await c.SetTextAsync(text);
        _notice.Text = Strings.Remote_Copied;
        _notice.IsVisible = true;
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;
}
