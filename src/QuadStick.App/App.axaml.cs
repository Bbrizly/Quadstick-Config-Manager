using Avalonia;
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

    public override void OnFrameworkInitializationCompleted()
    {
        CrashGuard.Install(); // before ANY window exists: nothing runs uncovered
        Theme.RegisterInto(this);
        Theme.Apply(Settings.Load().Theme);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
