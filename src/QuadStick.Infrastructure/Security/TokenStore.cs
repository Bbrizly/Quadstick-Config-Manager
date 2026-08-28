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

    public static ITokenStore Create() =>
        OperatingSystem.IsMacOS() ? new MacKeychainTokenStore(Service, Account)
        : OperatingSystem.IsWindows() ? new WindowsDpapiTokenStore()
        : new InMemoryTokenStore();
}

// macOS Keychain via the legacy generic-password API. Less interop than the
// CFDictionary path and supports in-place updates, so replacing a token never
// deletes the old working credential before the new value is accepted.
public class MacKeychainTokenStore : ITokenStore
{
    const string Sec = "/System/Library/Frameworks/Security.framework/Security";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

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
    static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(Sec)]
    static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(Sec)]
    static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(CoreFoundation)]
    static extern void CFRelease(IntPtr cf);

    public string? Load()
    {
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service,
            (uint)_account.Length, _account, out uint pwLen, out IntPtr pwData, out IntPtr itemRef);
        if (status != 0) return null;
        try
        {
            var buf = new byte[pwLen];
            Marshal.Copy(pwData, buf, 0, (int)pwLen);
            return Encoding.UTF8.GetString(buf);
        }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, pwData);
            if (itemRef != IntPtr.Zero) CFRelease(itemRef);
        }
    }

    public void Save(string refreshToken)
    {
        var pw = Encoding.UTF8.GetBytes(refreshToken);
        int find = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service,
            (uint)_account.Length, _account, out _, out IntPtr oldData, out IntPtr itemRef);

        if (find == 0)
        {
            try
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, oldData);
                oldData = IntPtr.Zero;
                int status = SecKeychainItemModifyAttributesAndData(itemRef, IntPtr.Zero, (uint)pw.Length, pw);
                if (status != 0) throw new InvalidOperationException($"Keychain update failed: {status}");
                return;
            }
            finally
            {
                if (oldData != IntPtr.Zero) _ = SecKeychainItemFreeContent(IntPtr.Zero, oldData);
                if (itemRef != IntPtr.Zero) CFRelease(itemRef);
            }
        }

        int add = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)_service.Length, _service,
            (uint)_account.Length, _account, (uint)pw.Length, pw, out IntPtr addedItem);
        try
        {
            if (add != 0) throw new InvalidOperationException($"Keychain save failed: {add}");
        }
        finally
        {
            if (addedItem != IntPtr.Zero) CFRelease(addedItem);
        }
    }

    public void Delete()
    {
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)_service.Length, _service,
            (uint)_account.Length, _account, out _, out IntPtr pwData, out IntPtr itemRef);
        if (status != 0) return;
        try
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, pwData);
            pwData = IntPtr.Zero;
            int deleted = SecKeychainItemDelete(itemRef);
            if (deleted != 0) throw new InvalidOperationException($"Keychain delete failed: {deleted}");
        }
        finally
        {
            if (pwData != IntPtr.Zero) _ = SecKeychainItemFreeContent(IntPtr.Zero, pwData);
            if (itemRef != IntPtr.Zero) CFRelease(itemRef);
        }
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
        WriteBytesAtomic(_path, enc);
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    static void WriteBytesAtomic(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var tmp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.qscm-tmp");
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}

// For tests and unsupported platforms. Google backup is disabled there, so no
// persistent credential is silently written in plaintext.
public class InMemoryTokenStore : ITokenStore
{
    string? _token;
    public string? Load() => _token;
    public void Save(string refreshToken) => _token = refreshToken;
    public void Delete() => _token = null;
}
