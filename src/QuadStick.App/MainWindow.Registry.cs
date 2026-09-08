namespace QuadStick.App;

public partial class MainWindow
{
    /// <summary>Resolve a public registry id, then hand its Google Sheet to the
    /// exact same import/review path used when a user pastes that Sheet by hand.
    /// A deep link can therefore open an editor, never install to hardware.</summary>
    internal async Task OpenRegistryProfileAsync(string profileId)
    {
        Status(Strings.Community_LoadingTheCommunityList2, StatusKind.Info);
        try
        {
            var result = await new ProfileRegistryClient().LoadAsync();
            var profile = result.Profiles.FirstOrDefault(p =>
                p.Id.Equals(profileId, StringComparison.Ordinal));
            if (profile is null)
            {
                Status(Strings.Community_TheCommunityListCouldNot, StatusKind.Error);
                return;
            }
            await ImportSheetsAsync(ProfileRegistryClient.SheetEditUrl(profile),
                onError: message => Status(message, StatusKind.Error), dialogOwner: this);
        }
        catch
        {
            Status(Strings.Community_TheCommunityListCouldNot, StatusKind.Error);
        }
    }
}
