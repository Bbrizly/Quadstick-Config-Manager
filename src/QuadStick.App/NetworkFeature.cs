using System.Collections.Frozen;

namespace QuadStick.App;

/// <summary>Whether this run of the app may use the network at all.</summary>
/// <remarks>
/// Off means off: no community list, no Google backup, no update check, no
/// analytics, and no HTTP client built, so there is nothing left that could
/// reach out by accident. The buttons that lead to those screens are not in
/// the layout either, because a button that opens a page saying "not
/// available" is still a button somebody has to try.
///
/// The free app leaves this on. A host executable sets it to false before the
/// first window, and that is the only supported time to set it: the buttons
/// read it while a window is being built.
/// </remarks>
public static class NetworkFeature
{
    public static bool Enabled { get; set; } = true;

    /// <summary>Google Drive and Sheets, and nothing else, for a host that
    /// keeps <see cref="Enabled"/> off. Read only by the Drive backup engine
    /// and Google sign-in, never by analytics, crash reports, the community
    /// list or the update check, which all still ask <see cref="Enabled"/>.
    /// Every request it lets through is checked against
    /// <see cref="GoogleOnlyHandler"/>'s list of Google hosts.</summary>
    /// <remarks>While Enabled is off, the engine only writes to sheets a
    /// profile is already linked to: a host decides which profiles go to
    /// Drive, one share at a time, and a plain save never starts a new one.</remarks>
    public static bool GoogleDriveOnly { get; set; }

    /// <summary>Whether the Drive engine and Google sign-in may run.</summary>
    public static bool DriveAllowed => Enabled || GoogleDriveOnly;
}

/// <summary>The one road to Google that Drive takes. Refuses any request that
/// is not HTTPS to a Google API host, so the Drive engine cannot be pointed
/// anywhere else by a bad URL, and a host that allows Drive alone can say so
/// and have it be checked rather than promised.</summary>
/// <remarks>Redirects are followed here, not by the handler underneath, so
/// every hop is checked against the same list. Only GET is ever followed: a
/// write that answers with a redirect is handed back as it is.</remarks>
public sealed class GoogleOnlyHandler : DelegatingHandler
{
    static readonly FrozenSet<string> Allowed = new[]
    {
        "www.googleapis.com",     // Drive: create, list, share, download
        "sheets.googleapis.com",  // Sheets: write the tabs
        "oauth2.googleapis.com",  // sign-in: the token exchange and refresh
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    const int MaxRedirects = 3;

    /// <summary>The hosts, as a copy: changing it changes nothing.</summary>
    public static IReadOnlyList<string> Hosts => Allowed.ToArray();

    /// <summary>The handler Drive and sign-in use: this, over a platform
    /// handler that never follows a redirect by itself.</summary>
    public static GoogleOnlyHandler Create() =>
        new(new HttpClientHandler { AllowAutoRedirect = false });

    public GoogleOnlyHandler(HttpMessageHandler inner) : base(inner) { }

    public static bool Allows(Uri? uri) =>
        uri is not null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && Allowed.Contains(uri.Host);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!Allows(request.RequestUri)) Refuse();
        var response = await base.SendAsync(request, ct);
        for (int hop = 0; hop < MaxRedirects; hop++)
        {
            var code = (int)response.StatusCode;
            if (code < 300 || code > 399 || request.Method != HttpMethod.Get
                || response.Headers.Location is not Uri location) return response;
            var next = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
            response.Dispose();
            if (!Allows(next)) Refuse();
            var again = new HttpRequestMessage(HttpMethod.Get, next);
            foreach (var h in request.Headers) again.Headers.TryAddWithoutValidation(h.Key, h.Value);
            request = again;
            response = await base.SendAsync(request, ct);
        }
        return response;
    }

    // Read by whoever is debugging, never shown as it is.
    static void Refuse() => throw new HttpRequestException("Only Google Drive and Sheets may be reached here.");
}
