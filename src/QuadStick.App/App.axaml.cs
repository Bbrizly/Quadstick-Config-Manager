using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace QuadStick.App;

public class App : Application
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
        CrashGuard.Install(); // before ANY window exists: nothing runs uncovered
        var settings = Settings.Load();
        // Before the first window, not after: a window reads its text once,
        // while it is being built.
        Localization.Apply(settings.Language);
        Theme.RegisterInto(this);
        Theme.Apply(settings.Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = WindowFor(desktop.Args);
            // Before returning control to the platform, subscribe to activation.
            // Avalonia 11 exposes TryGetFeature(Type); macOS delivers custom URL
            // schemes through IActivatableLifetime rather than argv.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is ProtocolActivatedEventArgs protocol &&
                        RegistryDeepLink.TryGetProfileId(protocol.Uri, out var id) &&
                        desktop.MainWindow is MainWindow main)
                        Dispatcher.UIThread.Post(() => _ = main.OpenRegistryProfileAsync(id));
                };
            }
            // Only the real app reads the stick, and it reads it for as long as
            // the app is open rather than while one page is showing. The
            // headless tests and the render tool build a MainWindow without
            // coming through here: no machine running those has a QuadStick,
            // and a thread parked on a USB enumeration per test window is a
            // cost the suite should not pay.
            (window as MainWindow)?.StartLiveInput();
            desktop.MainWindow = window;

            // Windows/unpackaged desktop activation arrives as the command-line
            // qcm:// argument. Post it so the window and its import dialog owner
            // are fully initialized first.
            if (window is MainWindow qcm &&
                RegistryDeepLink.TryGetProfileId(desktop.Args, out var profileId))
                Dispatcher.UIThread.Post(() => _ = qcm.OpenRegistryProfileAsync(profileId));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
