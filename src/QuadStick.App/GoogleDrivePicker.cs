using System.Net;
using System.Text;

namespace QuadStick.App;

/// <summary>
/// Google's desktop Picker is an OAuth authorization request with
/// trigger_onepick=true. It runs in the system browser and returns both an
/// authorization code and the explicitly selected Drive file ids to QCM's
/// loopback callback. Scope stays exactly drive.file.
/// </summary>
public sealed class GoogleDrivePicker
{
    const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    const string Scope = "https://www.googleapis.com/auth/drive.file";
    const string SheetsMime = "application/vnd.google-apps.spreadsheet";
    const string CsvMime = "text/csv";

    readonly GoogleAuth _auth;

    public GoogleDrivePicker(GoogleAuth auth) => _auth = auth;

    public async Task<IReadOnlyList<string>> PickProfilesAsync(
        Func<Uri, Task> launcher,
        bool allowMultiple = true,
        CancellationToken ct = default)
    {
        var verifier = GoogleAuth.CreateVerifier();
        var challenge = GoogleAuth.Challenge(verifier);
        var state = GoogleAuth.CreateVerifier();
        var (listener, port) = GoogleAuth.StartLoopback();
        var redirect = $"http://127.0.0.1:{port}/";

        try
        {
            await launcher(BuildAuthorizationUri(challenge, state, redirect, allowMultiple));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var callback = await AwaitPickerCallbackAsync(listener, state, timeout.Token);

            // Picker returns a NEW code and Google requires apps to exchange it.
            // Exchange only after state validation, so an attacker cannot make
            // QCM adopt their callback/token through the loopback port.
            await _auth.ExchangeCodeAsync(callback.Code, verifier, redirect, ct);
            return callback.FileIds;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    internal static Uri BuildAuthorizationUri(
        string challenge, string state, string redirect, bool allowMultiple)
    {
        var parameters = new List<string>
        {
            "client_id=" + Uri.EscapeDataString(GoogleAuth.ClientId),
            "redirect_uri=" + Uri.EscapeDataString(redirect),
            "response_type=code",
            "scope=" + Uri.EscapeDataString(Scope),
            "code_challenge=" + Uri.EscapeDataString(challenge),
            "code_challenge_method=S256",
            "state=" + Uri.EscapeDataString(state),
            "access_type=offline",
            "prompt=consent",
            "trigger_onepick=true",
            "mimetypes=" + Uri.EscapeDataString(SheetsMime + "," + CsvMime),
        };
        if (allowMultiple) parameters.Add("allow_multiple=true");
        return new Uri(AuthEndpoint + "?" + string.Join("&", parameters));
    }

    internal static async Task<GooglePickerCallback> AwaitPickerCallbackAsync(
        HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        HttpListenerContext context;
        try { context = await listener.GetContextAsync(); }
        catch when (ct.IsCancellationRequested)
        {
            throw new GooglePickerCancelledException();
        }

        var query = ParseQuery(context.Request.Url!.Query);
        void Respond(string message)
        {
            var bytes = Encoding.UTF8.GetBytes($"<!doctype html><html><body><p>{WebUtility.HtmlEncode(message)}</p></body></html>");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.Close();
        }

        // State is checked before trusting code OR selected file ids.
        if (!query.TryGetValue("state", out var state) || state != expectedState)
        {
            Respond(Strings.Auth_SignInFailedYouCan2);
            throw new GoogleAuthException("picker state mismatch");
        }

        if (query.TryGetValue("error", out var error))
        {
            Respond(Strings.Auth_SignInFailedYouCan);
            throw new GooglePickerCancelledException(error);
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            Respond(Strings.Auth_SignInFailedYouCan3);
            throw new GoogleAuthException("picker callback has no authorization code");
        }

        var ids = query.TryGetValue("picked_file_ids", out var picked)
            ? picked.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsPlausibleDriveId)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : new List<string>();

        Respond(Strings.Auth_YouAreSignedInYou);
        if (ids.Count == 0) throw new GooglePickerCancelledException();
        return new GooglePickerCallback(code, ids);
    }

    // Callback ids are still validated with files.get before linking. This
    // lightweight check only rejects control characters/absurd input before it
    // can become a URL path or persistent key.
    internal static bool IsPlausibleDriveId(string id) =>
        id.Length is >= 8 and <= 256 && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? "" : part[(eq + 1)..];
            values[Uri.UnescapeDataString(key.Replace('+', ' '))] =
                Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return values;
    }
}

internal sealed record GooglePickerCallback(string Code, IReadOnlyList<string> FileIds);

public sealed class GooglePickerCancelledException : GoogleAuthException
{
    public GooglePickerCancelledException(string? detail = null)
        : base(detail ?? "picker cancelled") { }
}
