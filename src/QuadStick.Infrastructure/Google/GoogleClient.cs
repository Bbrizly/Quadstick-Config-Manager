#if !GOOGLE_CLIENT_LOCAL
namespace QuadStick.Infrastructure.Google;

// Placeholder Google OAuth client. Real id and secret live in the gitignored
// GoogleClient.Local.cs in this Infrastructure folder. The secret is not
// confidential for installed apps; keeping it out of the public repository
// merely avoids handing it to scrapers.
static class GoogleClient
{
    public const string Id = "REPLACE-ME.apps.googleusercontent.com";
    public const string Secret = "";
}
#endif
