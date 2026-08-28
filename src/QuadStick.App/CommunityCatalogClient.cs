using QuadStick.Application.Community;
using QuadStick.Infrastructure.Community;

namespace QuadStick.App;

/// <summary>Presentation compatibility facade while the window migrates to a
/// directly injected CommunityCatalogUseCase. It contains no HTTP/cache policy.</summary>
public sealed class CommunityCatalogClient
{
    readonly CommunityCatalogUseCase _useCase;

    internal const string CatalogUrl = HttpCommunityCatalogSource.CatalogUrl;
    public static string DefaultCachePath => HttpCommunityCatalogSource.DefaultCachePath;
    public const int MaxReplyBytes = HttpCommunityCatalogSource.MaxReplyBytes;
    public const int MaxRows = HttpCommunityCatalogSource.MaxRows;
    public const int MaxFieldChars = HttpCommunityCatalogSource.MaxFieldChars;

    public CommunityCatalogClient() : this(CompositionRoot.CommunityCatalog) { }

    public CommunityCatalogClient(HttpMessageHandler handler, string cachePath)
        : this(new CommunityCatalogUseCase(new HttpCommunityCatalogSource(handler, cachePath))) { }

    internal CommunityCatalogClient(ICommunityCatalogSource source)
        : this(new CommunityCatalogUseCase(source)) { }

    internal CommunityCatalogClient(CommunityCatalogUseCase useCase) => _useCase = useCase;

    public Task<CommunityCatalogResult> LoadAsync(bool refresh = false, CancellationToken ct = default) =>
        _useCase.LoadAsync(refresh, ct);

    internal static (List<CommunityProfile> Profiles, int Skipped) Parse(string json) =>
        HttpCommunityCatalogSource.Parse(json);
}