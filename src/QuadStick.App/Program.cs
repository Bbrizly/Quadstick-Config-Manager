using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Avalonia;

namespace QuadStick.App;

// Public so another host executable can start this app: it builds the same
// AppBuilder and gets the same native-library fix.
public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ProtocolRegistration.EnsureCurrentUser();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // BuildAvaloniaApp is public and may be called more than once. Without the
    // guard each call hangs another resolver off the load context.
    static bool _nativeFallbackInstalled;

    // The Mac App Store installs the bundle under the store name, which is
    // "Quadstick: Config Manager". CoreCLR passes the native search path to
    // the runtime as a colon-separated list, so that colon cuts the path in
    // half and libSkiaSharp is never found: the app aborts before it draws a
    // single pixel (App Store review, 2026-07-21). AppContext.BaseDirectory is
    // a single string, so it survives the colon. Load from there by hand when
    // the normal search fails.
    static void InstallNativeLibraryFallback()
    {
        if (_nativeFallbackInstalled) return;
        _nativeFallbackInstalled = true;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            foreach (var file in new[] { name, name + ".dylib", "lib" + name + ".dylib" })
            {
                var path = Path.Combine(AppContext.BaseDirectory, file);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                    return handle;
            }
            return IntPtr.Zero;
        };
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        // First, before Configure: registering the resolver later can be too
        // late for the first native load.
        InstallNativeLibraryFallback();
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
