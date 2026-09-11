using QuadStick.Format;

namespace QuadStick.App;

public partial class MainWindow
{
    /// <summary>Resolve a public Adaptive Profiles id, fetch the exact CSV
    /// snapshot that passed registry review, then open it in QCM's normal editor.
    /// A website deep link can never write directly to hardware.</summary>
    internal async Task OpenRegistryProfileAsync(string profileId)
    {
        Status(Strings.Community_LoadingTheCommunityList2, StatusKind.Info);
        try
        {
            var client = new ProfileRegistryClient();
            var catalog = await client.LoadAsync(refresh: true);
            var profile = catalog.Profiles.FirstOrDefault(p =>
                p.Id.Equals(profileId, StringComparison.Ordinal));
            if (profile is null)
            {
                Status(Strings.Community_TheCommunityListCouldNot, StatusKind.Error);
                return;
            }

            var csv = await client.DownloadCsvAsync(profile);
            var file = ProfileFile.Load(csv);
            if (file.Document.Sheets.Count == 0)
                throw new ProfileRegistryException("Profile snapshot contains no configuration sheets.");

            // Keep savePath null. A registry profile is an imported starting point;
            // QCM must ask where to save instead of overwriting a cache file.
            OpenInEditor(file, savePath: null, ProfileSource.File);
        }
        catch
        {
            Status(Strings.Community_TheCommunityListCouldNot, StatusKind.Error);
        }
    }
}
