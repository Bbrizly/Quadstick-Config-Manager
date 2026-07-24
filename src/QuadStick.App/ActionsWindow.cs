using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using QuadStick.Format;

namespace QuadStick.App;

// The translation table: every name this profile gives one of its outputs.
//
// There is no table stored anywhere. The table is the names already sitting in
// column L across the profile, so this window reads them off the rows. Renaming
// here updates every row using that name in one undo step, which is the one
// thing you cannot do row by row.
public class ActionsWindow : Window
{
    readonly MainWindow _owner;
    readonly StackPanel _rows = new() { Spacing = 8 };
    readonly TextBlock _empty = new()
    {
        Text = "No names yet. Open a mapping, pick its output, and type a name for it. "
             + "The name is yours; the file still holds the real button, so the QuadStick works the same.",
        TextWrapping = TextWrapping.Wrap,
        Classes = { "muted" },
    };

    public ActionsWindow(MainWindow owner)
    {
        _owner = owner;
        Title = "Action names";
        Width = Math.Min(560 * owner.UiScale, 1000);
        Height = Math.Min(460 * owner.UiScale, 820);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button
        {
            Content = "Done", Classes = { "primary" }, IsCancel = true,
            FontSize = Size("SubheadSize"), Padding = new Thickness(28, 12), MinWidth = 150,
        };
        AutomationProperties.SetName(close, "Close action names");
        close.Click += (_, _) => Close();
        Opened += (_, _) => close.Focus();

        var body = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "Your own name for a button, like Shoot for the left mouse click. "
                         + "The name is per row, so the same button can be Shoot in one mode and Select in another.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = Size("BodySize"),
                    Classes = { "muted" },
                },
                _empty,
                new ScrollViewer
                {
                    Content = _rows,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    MaxHeight = 320,
                },
            },
        };

        Content = MainWindow.ZoomWrap(new DockPanel
        {
            Children =
            {
                new Border
                {
                    [DockPanel.DockProperty] = Dock.Bottom,
                    Padding = new Thickness(24, 12),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { close },
                    },
                },
                new ScrollViewer { Content = body },
            },
        }, owner.UiScale);

        Build();
    }

    static double Size(string tokenKey) => (double)Application.Current!.FindResource(tokenKey)!;

    // Rebuilding detaches the name boxes, and a detached box raises LostFocus.
    // That would commit its old text against a list that has just changed.
    // Same guard ModesWindow uses, for the same reason.
    bool _rebuilding;

    void Build()
    {
        _rebuilding = true;
        _rows.Children.Clear();
        var file = _owner.OpenFile;
        var names = file?.ActionNames() ?? new List<string>();
        _empty.IsVisible = names.Count == 0;
        if (file is not null)
        {
            var tokens = file.ActionTokens();
            foreach (var n in names) _rows.Children.Add(Row(file, n, tokens.GetValueOrDefault(n, "")));
        }
        _rebuilding = false;
    }

    Control Row(ProfileFile file, string name, string token)
    {
        int used = file.Document.Sheets
            .Where(s => s.Type == SheetType.ProfileName)
            .SelectMany(s => s.Bindings).Count(b => b.ActionName == name);

        var box = new TextBox
        {
            Text = name, Width = 240, MaxLength = ProfileFile.MaxActionName,
            FontSize = Size("BodySize"), VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(box, $"Name for {token}. Currently {name}.");

        void Commit()
        {
            if (_rebuilding) return;
            var typed = (box.Text ?? "").Trim();
            if (typed == name) return;
            if (!file.RenameAction(name, typed))
            {
                // A blank name, one too long, or one that reads as a real
                // output token. Put the old one back rather than explain.
                box.Text = name;
                return;
            }
            _owner.ActionsChanged($"Renamed {name} to {typed}.");
            Build();
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };

        var detail = new TextBlock
        {
            Text = $"{token} · {used} row{(used == 1 ? "" : "s")}",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = Size("SmallSize"),
            Classes = { "muted" },
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { box, detail },
        };
    }
}
