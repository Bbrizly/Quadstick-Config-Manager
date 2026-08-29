using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace QuadStick.App;

public class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Here, not in OnFrameworkInitializationCompleted: the headless test host
        // and the render tool build windows without ever reaching that, and a
        // style that cannot find its numbers throws on the first control.
        Style.RegisterInto(this);
    }

    /// <summary>Which window the app opens with. --gallery opens the appearance
    /// workbench, a build tool. Nothing in the app links to it.</summary>
    internal static Window WindowFor(IReadOnlyList<string>? args) =>
        args is not null && args.Contains("--gallery") ? new GalleryWindow() : new MainWindow();

    public override void OnFrameworkInitializationCompleted()
    {
        CrashGuard.Install();
        var settings = Settings.Load();
        Localization.Apply(settings.Language);
        Theme.RegisterInto(this);
        Theme.Apply(settings.Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = WindowFor(desktop.Args);
        base.OnFrameworkInitializationCompleted();
    }
}
