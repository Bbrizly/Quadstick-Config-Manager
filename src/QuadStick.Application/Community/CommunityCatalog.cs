namespace QuadStick.Application.Community;

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

public interface ICommunityCatalogSource
{
    Task<CommunityCatalogResult> LoadAsync(bool refresh = false, CancellationToken cancellationToken = default);
}

public sealed class CommunityCatalogUseCase
{
    readonly ICommunityCatalogSource _source;

    public CommunityCatalogUseCase(ICommunityCatalogSource source) => _source = source;

    public Task<CommunityCatalogResult> LoadAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default) =>
        _source.LoadAsync(refresh, cancellationToken);
}