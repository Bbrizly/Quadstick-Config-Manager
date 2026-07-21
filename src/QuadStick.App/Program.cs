using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Avalonia;

namespace QuadStick.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        InstallNativeLibraryFallback();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // The Mac App Store installs the bundle under the store name, which is
    // "Quadstick: Config Manager". CoreCLR passes the native search path to
    // the runtime as a colon-separated list, so that colon cuts the path in
    // half and libSkiaSharp is never found: the app aborts before it draws a
    // single pixel (App Store review, 2026-07-21). AppContext.BaseDirectory is
    // a single string, so it survives the colon. Load from there by hand when
    // the normal search fails.
    static void InstallNativeLibraryFallback() =>
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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
