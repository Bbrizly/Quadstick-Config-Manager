using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace QuadStick.App;

// Refresh token at rest. Platform stores keep it out of plain settings.
public interface ITokenStore
{
    string? Load();
    void Save(string refreshToken);
    void Delete();
}

public static class TokenStore
{
    const string Service = "QuadStick Config Manager";
    const string Account = "google-drive";

    // Store for the current OS.
    public static ITokenStore Create() =>
        OperatingSystem.IsMacOS() ? new MacKeychainTokenStore(Service, Account)
        : OperatingSystem.IsWindows() ? new WindowsDpapiTokenStore()
        : new InMemoryTokenStore();
}

// macOS Keychain via the legacy generic-password API. Less interop than
// the CFDictionary path, fine for one secret.
public class MacKeychainTokenStore : ITokenStore
{
    const string Sec = "/System/Library/Frameworks/Security.framework/Security";

    readonly byte[] _service;
    readonly byte[] _account;

    public MacKeychainTokenStore(string service, string account)
    {
        _service = Encoding.UTF8.GetBytes(service);
        _account = Encoding.UTF8.GetBytes(account);
    }

    [DllImport(Sec)]
    static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLen, byte[] service,
        uint accountLen, byte[] account, uint pwLen, byte[] pw, out IntPtr itemRef);

    [DllImport(Sec)]
    static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceLen, byte[] service,
        uint accountLen, byte[] account, out uint pwLen, out IntPtr pwData, out IntPtr itemRef);

    [DllImport(Sec)]
    static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(Sec)]
    static extern int SecKeychainItemDelete(IntPtr itemRef);

    public string? Load()
    {
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service,
            (uint)_account.Length, _account, out uint pwLen, out IntPtr pwData, out _);
        if (status != 0) return null; // errSecItemNotFound and friends
        try
        {
            var buf = new byte[pwLen];
            Marshal.Copy(pwData, buf, 0, (int)pwLen);
            return Encoding.UTF8.GetString(buf);
        }
        finally { _ = SecKeychainItemFreeContent(IntPtr.Zero, pwData); }
    }

    // Delete any existing item then add, so a re-save updates in place.
    public void Save(string refreshToken)
    {
        Delete();
        var pw = Encoding.UTF8.GetBytes(refreshToken);
        int status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)_service.Length, _service,
            (uint)_account.Length, _account, (uint)pw.Length, pw, out _);
        if (status != 0) throw new InvalidOperationException($"Keychain save failed: {status}");
    }

    public void Delete()
    {
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service,
            (uint)_account.Length, _account, out _, out IntPtr pwData, out IntPtr itemRef);
        if (status != 0) return;
        _ = SecKeychainItemFreeContent(IntPtr.Zero, pwData);
        _ = SecKeychainItemDelete(itemRef);
    }
}

// Windows DPAPI (CurrentUser) to a file under AppData.
[SupportedOSPlatform("windows")]
public class WindowsDpapiTokenStore : ITokenStore
{
    readonly string _path;

    public WindowsDpapiTokenStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuadStickConfigManager");
        _path = Path.Combine(dir, "google-drive.token");
    }

    public string? Load()
    {
        // Corrupt or unreadable file means "not connected", never a crash.
        // The user just reconnects.
        try
        {
            if (!File.Exists(_path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    public void Save(string refreshToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, enc);
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

// For tests.
public class InMemoryTokenStore : ITokenStore
{
    string? _token;
    public string? Load() => _token;
    public void Save(string refreshToken) => _token = refreshToken;
    public void Delete() => _token = null;
}
