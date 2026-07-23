#if !GOOGLE_CLIENT_LOCAL
namespace QuadStick.App;

// Placeholder Google OAuth client. The real id and secret live in
// GoogleClient.Local.cs, which is gitignored so they stay out of the public
// repo. To build a connected app, copy this class into GoogleClient.Local.cs
// (drop the #if lines) and paste in your own Desktop-app client from the
// Google Cloud console. The build picks the local file up automatically.
//
// Note the secret is not confidential for installed apps: Google requires it
// at the token endpoint but expects it to ship in the binary. Keeping it out
// of the repo just avoids handing it to scrapers.
static class GoogleClient
{
    public const string Id = "REPLACE-ME.apps.googleusercontent.com";
    public const string Secret = "";
}
#endif
