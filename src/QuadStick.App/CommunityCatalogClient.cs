namespace QuadStick.App;

/// <summary>Presentation compatibility facade. The window/tests keep the old
/// entry point while Application owns the operation and Infrastructure owns
/// HTTP/cache behavior.</summary>
public sealed class CommunityCatalogClient
{
    readonly CommunityCatalogUseCase _useCase;

    internal const string CatalogUrl = HttpCommunityCatalogSource.CatalogUrl;
    public static string DefaultCachePath => HttpCommunityCatalogSource.DefaultCachePath;
    public const int MaxReplyBytes = HttpCommunityCatalogSource.MaxReplyBytes;
    public const int MaxRows = HttpCommunityCatalogSource.MaxRows;
    public const int MaxFieldChars = HttpCommunityCatalogSource.MaxFieldChars;

    public CommunityCatalogClient() : this(new HttpCommunityCatalogSource()) { }

    public CommunityCatalogClient(HttpMessageHandler handler, string cachePath)
        : this(new HttpCommunityCatalogSource(handler, cachePath)) { }

    internal CommunityCatalogClient(ICommunityCatalogSource source) =>
        _useCase = new CommunityCatalogUseCase(source);

    public Task<CommunityCatalogResult> LoadAsync(bool refresh = false, CancellationToken ct = default) =>
        _useCase.LoadAsync(refresh, ct);

    internal static (List<CommunityProfile> Profiles, int Skipped) Parse(string json) =>
        HttpCommunityCatalogSource.Parse(json);
}
