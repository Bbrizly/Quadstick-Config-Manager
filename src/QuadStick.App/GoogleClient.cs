#if !GOOGLE_CLIENT_LOCAL
namespace QuadStick.App;

// Placeholder Google OAuth client. Real id and secret live in the gitignored
// GoogleClient.Local.cs. To build a connected app, copy this class there (drop
// the #if lines) and paste your own Desktop-app client from the Google Cloud
// console. The build picks up the local file automatically.
//
// The secret is not confidential for installed apps: Google needs it at the
// token endpoint but expects it to ship in the binary. Keeping it out of the
// repo just avoids handing it to scrapers.
static class GoogleClient
{
    public const string Id = "REPLACE-ME.apps.googleusercontent.com";
    public const string Secret = "";
}
#endif
