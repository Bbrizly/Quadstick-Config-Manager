using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuadStick.App;

// OAuth 2.0 installed-app flow with PKCE. Scope: drive.file only.
public class GoogleAuth
{
    // From GoogleClient (local file if present, else placeholder).
    // Google needs the secret for Desktop clients even with PKCE. Not confidential for installed apps.
    public const string ClientId = GoogleClient.Id;
    public const string ClientSecret = GoogleClient.Secret;

    // False on the placeholder and on Linux (no persistent store: it would
    // drop the refresh token every restart). Callers show "not set up yet".
    public static bool IsConfigured =>
        !ClientId.StartsWith("REPLACE-ME")
        && (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows());

    const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    const string Scope = "https://www.googleapis.com/auth/drive.file";

    readonly HttpClient _http;
    readonly ITokenStore _store;

    string? _accessToken;
    DateTimeOffset _accessExpiry;

    // Drive backup used to make only one request at a time, but linked-sheet
    // sync will have independent change tracking and content requests. Without
    // a gate, two stale-token callers can both refresh at once. Google normally
    // tolerates that, but it wastes requests and makes token state needlessly
    // racy. The second caller re-checks the cache after it acquires the gate.
    readonly SemaphoreSlim _refreshGate = new(1, 1);

    public GoogleAuth(ITokenStore store, HttpMessageHandler? handler = null)
    {
        _store = store;
        _http = new HttpClient(handler ?? new HttpClientHandler());
    }

    // PKCE verifier: 32 random bytes, base64url (43 chars).
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    // S256 challenge: base64url(SHA256(ASCII(verifier))).
    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Interactive sign-in. Launcher opens the system browser (Google blocks
    // embedded webviews). ct cancels; 2-minute cap backs it up.
    public async Task SignInAsync(Func<Uri, Task> launcher, CancellationToken ct = default)
    {
        var verifier = CreateVerifier();
        var challenge = Challenge(verifier);
        var state = CreateVerifier();
        var (listener, port) = StartLoopback();
        var redirect = $"http://127.0.0.1:{port}/";
        try
        {
            await launcher(new Uri(BuildAuthUrl(challenge, state, redirect)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var code = await AwaitCodeAsync(listener, state, timeout.Token);
            await ExchangeCodeAsync(code, verifier, redirect, ct);
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    // Bind a random loopback port. Retry on bind failure.
    public static (HttpListener listener, int port) StartLoopback()
    {
        var rand = new Random();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int port = rand.Next(1024, 65500);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); return (listener, port); }
            catch (HttpListenerException) { }
            catch (SocketException) { }
        }
        throw new GoogleAuthException("Could not bind a loopback port for sign-in.");
    }

    static string BuildAuthUrl(string challenge, string state, string redirect) =>
        AuthEndpoint + "?" + string.Join("&", new[]
        {
            "client_id=" + Uri.EscapeDataString(ClientId),
            "redirect_uri=" + Uri.EscapeDataString(redirect),
            "response_type=code",
            "scope=" + Uri.EscapeDataString(Scope),
            "code_challenge=" + challenge,
            "code_challenge_method=S256",
            "state=" + state,
            "access_type=offline",
            "prompt=consent",
        });

    // Wait for the redirect, check state, return the code.
    // Bad or missing state is rejected. ct stops the listener.
    public static async Task<string> AwaitCodeAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); }
        catch when (ct.IsCancellationRequested) { throw new GoogleAuthException("Sign-in timed out or was cancelled."); }

        var q = ParseQuery(ctx.Request.Url!.Query);

        void Respond(string message)
        {
            var bytes = Encoding.UTF8.GetBytes(string.Format(CultureInfo.CurrentCulture, Strings.Auth_DoctypeHtmlHtmlBodyP, message));
            ctx.Response.ContentType = "text/html";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        if (q.TryGetValue("error", out var err)) { Respond(Strings.Auth_SignInFailedYouCan); throw new GoogleAuthException(err); }
        if (!q.TryGetValue("state", out var s) || s != expectedState) { Respond(Strings.Auth_SignInFailedYouCan2); throw new GoogleAuthException("state mismatch on the loopback callback"); }
        if (!q.TryGetValue("code", out var code)) { Respond(Strings.Auth_SignInFailedYouCan3); throw new GoogleAuthException("no authorization code on the callback"); }

        Respond(Strings.Auth_YouAreSignedInYou);
        return code;
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            var val = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
            dict[Uri.UnescapeDataString(key)] = val;
        }
        return dict;
    }

    // Trade the authorization code for tokens and store the refresh token.
    public async Task ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        };
        using var resp = await PostTokenAsync(form, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        CacheAccess(doc.RootElement);
        if (doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.GetString() is string token)
            _store.Save(token);
    }

    // Cached access token, refreshed with the stored refresh token when stale.
    // Double-check after the gate: while this caller waited, another one may
    // already have refreshed the token.
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (FreshAccessToken() is string cached) return cached;

        await _refreshGate.WaitAsync(ct);
        try
        {
            if (FreshAccessToken() is string refreshed) return refreshed;

            var refresh = _store.Load() ?? throw new GoogleAuthException("Not connected to Google.");
            var form = new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["refresh_token"] = refresh,
                ["grant_type"] = "refresh_token",
            };
            using var resp = await PostTokenAsync(form, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            CacheAccess(doc.RootElement);
            return _accessToken!;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    string? FreshAccessToken() =>
        _accessToken != null && DateTimeOffset.UtcNow < _accessExpiry - TimeSpan.FromSeconds(60)
            ? _accessToken
            : null;

    async Task<HttpResponseMessage> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        if (ClientSecret.Length > 0) form["client_secret"] = ClientSecret;
        var resp = await _http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if (resp.IsSuccessStatusCode) return resp;

        var status = (int)resp.StatusCode;
        var body = await resp.Content.ReadAsStringAsync(ct);
        string? error = null;
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var e)) error = e.GetString();
        }
        catch { }
        resp.Dispose();
        // Revoked or expired refresh token. Caller pauses backup and shows Reconnect.
        if (error == "invalid_grant") throw new GoogleAuthRevokedException();
        throw new GoogleAuthException($"Token endpoint returned {status}: {error ?? body}");
    }

    void CacheAccess(JsonElement root)
    {
        _accessToken = root.GetProperty("access_token").GetString();
        var seconds = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _accessExpiry = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }
}

public class GoogleAuthException : Exception
{
    public GoogleAuthException(string message) : base(message) { }
}

// Distinct so the caller can pause backup and show Reconnect.
public class GoogleAuthRevokedException : GoogleAuthException
{
    public GoogleAuthRevokedException()
        : base(Strings.Auth_TheGoogleConnectionWasRevoked) { }
}
