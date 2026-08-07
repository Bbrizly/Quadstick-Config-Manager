using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace QuadStick.App;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Here, not in OnFrameworkInitializationCompleted: the headless test
        // host and the render tool both load the XAML and then build windows
        // without ever completing initialization, and a style that cannot
        // find its own numbers throws on the first control it touches.
        Style.RegisterInto(this);
    }

    /// <summary>Which window the app opens with. --gallery opens the
    /// appearance workbench instead: a tool for working on the look, not a
    /// screen of the program. Nothing in the app links to it and no user is
    /// ever handed it.</summary>
    internal static Window WindowFor(IReadOnlyList<string>? args) =>
        args is not null && args.Contains("--gallery") ? new GalleryWindow() : new MainWindow();

    public override void OnFrameworkInitializationCompleted()
    {
        CrashGuard.Install(); // before ANY window exists: nothing runs uncovered
        Theme.RegisterInto(this);
        Theme.Apply(Settings.Load().Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = WindowFor(desktop.Args);
        base.OnFrameworkInitializationCompleted();
    }
}
