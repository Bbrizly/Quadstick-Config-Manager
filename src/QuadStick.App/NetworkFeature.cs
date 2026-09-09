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
}
