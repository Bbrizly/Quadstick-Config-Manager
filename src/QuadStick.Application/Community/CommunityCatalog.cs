namespace QuadStick.App;

/// <summary>One game row from the official QuadStick catalog.</summary>
public sealed record CommunityProfile(
    string Name,
    string SheetId,
    string CsvName,
    string Connection,
    string Notes,
    string Pointer);

/// <summary>Profiles plus how they were obtained, so presentation can be honest
/// about stale data and dropped rows.</summary>
public sealed record CommunityCatalogResult(
    IReadOnlyList<CommunityProfile> Profiles,
    bool FromCache,
    int SkippedRows);

public sealed class CommunityCatalogException : Exception
{
    public CommunityCatalogException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Provider port for the official shared-profile catalog.</summary>
public interface ICommunityCatalogSource
{
    Task<CommunityCatalogResult> LoadAsync(bool refresh = false, CancellationToken cancellationToken = default);
}

/// <summary>Application operation for loading/refreshing the community catalog.
/// Caching, HTTP and JSON provider details remain outside Application.</summary>
public sealed class CommunityCatalogUseCase
{
    readonly ICommunityCatalogSource _source;

    public CommunityCatalogUseCase(ICommunityCatalogSource source) => _source = source;

    public Task<CommunityCatalogResult> LoadAsync(bool refresh = false, CancellationToken cancellationToken = default) =>
        _source.LoadAsync(refresh, cancellationToken);
}
