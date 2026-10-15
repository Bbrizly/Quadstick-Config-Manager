using System.Runtime.InteropServices;
using QuadStick.Format;

namespace QuadStick.App;

// The Mac App Store build is sandboxed, and the sandbox will not let it read a
// plugged-in QuadStick until the person picks the drive once. A security-scoped
// bookmark keeps that grant for every launch after. Avalonia 11.1 has no real
// bookmarks on macOS, so this asks Foundation directly.
static class MacDriveAccess
{
    // A normal build reads the drive without asking.
    internal static bool Needed => OperatingSystem.IsMacOS() && MacGamepad.Sandboxed;

    // Beside settings, not in them: MainWindow saves its own settings copy and
    // would write over a list changed from here.
    internal static string StorePath =>
        Path.Combine(Path.GetDirectoryName(Settings.DefaultPath)!, "drive-access.txt");

    const nuint CreateWithSecurityScope = 1 << 11;
    const nuint ResolveWithSecurityScope = 1 << 10, ResolveWithoutUI = 1 << 8, ResolveWithoutMounting = 1 << 9;

    /// <summary>Keeps access to a drive the person just picked. False when
    /// macOS would not make a bookmark for it.</summary>
    internal static bool Remember(string root)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var data = Msg(Url(root), Sel("bookmarkDataWithOptions:includingResourceValuesForKeys:relativeToURL:error:"),
                CreateWithSecurityScope, IntPtr.Zero, IntPtr.Zero, out _);
            if (data == IntPtr.Zero) return false;
            // Compared as macOS resolves them, since /var is really /private/var.
            var fresh = Base64(data);
            var path = Resolve(fresh, out _);
            Store(Stored().Where(b => Resolve(b, out _) != path).Append(fresh));
            return true;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>Reopens every remembered drive that is mounted but not yet
    /// readable. Runs before each scan, so a stick plugged in after launch is
    /// found too.</summary>
    internal static void Restore()
    {
        var bookmarks = Stored().ToList();
        if (bookmarks.Count == 0) return;
        var pool = objc_autoreleasePoolPush();
        try
        {
            foreach (var b in bookmarks)
            {
                var url = IntPtr.Zero;
                var path = Resolve(b, out url);
                if (path is null || !Directory.Exists(path) || Device.IsInstallTarget(path)) continue;
                MsgBool(url, Sel("startAccessingSecurityScopedResource"));
            }
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    // The path a bookmark points at, or null when its drive is not mounted or
    // the bookmark no longer resolves.
    // ponytail: a stale bookmark still resolves and is not refreshed; if one
    // stops resolving, the button comes back and one pick fixes it.
    internal static string? Resolve(string bookmark, out IntPtr url)
    {
        url = IntPtr.Zero;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(bookmark); } catch (FormatException) { return null; }
        var data = MsgData(Class("NSData"), Sel("dataWithBytes:length:"), bytes, (nuint)bytes.Length);
        url = MsgResolve(Class("NSURL"), Sel("URLByResolvingBookmarkData:options:relativeToURL:bookmarkDataIsStale:error:"),
            data, ResolveWithSecurityScope | ResolveWithoutUI | ResolveWithoutMounting, IntPtr.Zero, out _, out _);
        if (url == IntPtr.Zero) return null;
        var path = Marshal.PtrToStringUTF8(Msg(Msg(url, Sel("path")), Sel("UTF8String")));
        return path is null ? null : Path.TrimEndingDirectorySeparator(path);
    }

    static IEnumerable<string> Stored()
    {
        try { return File.ReadAllLines(StorePath).Where(l => l.Length > 0).ToArray(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    static void Store(IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllLines(StorePath, lines);
    }

    static IntPtr Url(string path) =>
        MsgStr(Class("NSURL"), Sel("fileURLWithPath:"), MsgUtf8(Class("NSString"), Sel("stringWithUTF8String:"), Z(path)));

    static string Base64(IntPtr data)
    {
        var bytes = new byte[(int)MsgLen(data, Sel("length"))];
        Marshal.Copy(Msg(data, Sel("bytes")), bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    const string Objc = "/usr/lib/libobjc.A.dylib";
    [DllImport(Objc)] static extern IntPtr objc_getClass(byte[] name);
    [DllImport(Objc)] static extern IntPtr sel_registerName(byte[] name);
    static IntPtr Class(string name) => objc_getClass(Z(name));
    static IntPtr Sel(string name) => sel_registerName(Z(name));
    static byte[] Z(string s) => System.Text.Encoding.UTF8.GetBytes(s + "\0");
    [DllImport(Objc)] static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Objc)] static extern void objc_autoreleasePoolPop(IntPtr pool);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr Msg(IntPtr o, IntPtr s);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern byte MsgBool(IntPtr o, IntPtr s);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern nuint MsgLen(IntPtr o, IntPtr s);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr MsgStr(IntPtr o, IntPtr s, IntPtr a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr MsgUtf8(IntPtr o, IntPtr s, byte[] a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr MsgData(IntPtr o, IntPtr s, byte[] b, nuint n);
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    static extern IntPtr Msg(IntPtr o, IntPtr s, nuint opts, IntPtr keys, IntPtr rel, out IntPtr err);
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    static extern IntPtr MsgResolve(IntPtr o, IntPtr s, IntPtr data, nuint opts, IntPtr rel, out byte stale, out IntPtr err);
}
