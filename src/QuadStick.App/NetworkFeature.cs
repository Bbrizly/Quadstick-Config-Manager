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
public sealed class GoogleOnlyHandler : DelegatingHandler
{
    public static readonly IReadOnlySet<string> Hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "www.googleapis.com",     // Drive: create, list, share, download
        "sheets.googleapis.com",  // Sheets: write the tabs
        "oauth2.googleapis.com",  // sign-in: the token exchange and refresh
    };

    public GoogleOnlyHandler(HttpMessageHandler inner) : base(inner) { }

    public static bool Allows(Uri? uri) =>
        uri is not null && uri.Scheme == Uri.UriSchemeHttps && Hosts.Contains(uri.Host);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Allows(request.RequestUri)
            ? base.SendAsync(request, ct)
            : throw new HttpRequestException("Only Google Drive and Sheets may be reached here.");
}
