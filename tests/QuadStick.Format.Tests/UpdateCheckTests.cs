using System.Net;
using System.Text;
using QuadStick.App;
using Xunit;

namespace QuadStick.Format.Tests;

// The app ships from a GitHub release and has never told anyone a new one
// exists. Nothing here downloads or replaces anything: the answer is a
// sentence and a link, and every outcome, including a failed check, says
// something the user can act on.
public class UpdateCheckTests
{
    class FakeHandler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
        public HttpRequestMessage? Last;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Last = req;
            return Task.FromResult(_reply(req));
        }
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static string Release(string tag, bool prerelease = false) =>
        $"{{\"tag_name\":\"{tag}\",\"html_url\":\"https://github.com/x/y/releases/{tag}\","
        + $"\"prerelease\":{(prerelease ? "true" : "false")}}}";

    static Task<UpdateResult> Check(Func<HttpRequestMessage, HttpResponseMessage> reply,
        string current = "1.6.0") => Check(new FakeHandler(reply), current);

    static Task<UpdateResult> Check(FakeHandler handler, string current = "1.6.0") =>
        UpdateCheck.LatestAsync(new HttpClient(handler), current);

    [Fact]
    public async Task A_newer_release_is_named_with_its_download_page()
    {
        var result = await Check(_ => Json(Release("v1.7.0")));

        Assert.True(result.IsNewer);
        Assert.Contains("1.6.0", result.Message);
        Assert.Contains("1.7.0", result.Message);
        Assert.Equal("https://github.com/x/y/releases/v1.7.0", result.DownloadUrl);
    }

    [Fact]
    public async Task The_same_version_says_you_are_up_to_date()
    {
        var result = await Check(_ => Json(Release("v1.6.0")));

        Assert.False(result.IsNewer);
        Assert.Contains("latest", result.Message);
    }

    // Running something newer than the last release (a local build) is not an
    // update, and must never be offered as one.
    [Fact]
    public async Task A_release_older_than_this_copy_is_not_an_update()
    {
        var result = await Check(_ => Json(Release("v1.5.0")));

        Assert.False(result.IsNewer);
    }

    [Fact]
    public async Task A_preview_release_says_it_is_a_preview()
    {
        var result = await Check(_ => Json(Release("v1.7.0", prerelease: true)));

        Assert.Contains("preview", result.Message);
    }

    // No network is the ordinary case for a device this app is often used
    // beside, and it must not look like a bug.
    [Fact]
    public async Task No_network_says_so_and_still_says_which_version_you_are_on()
    {
        var result = await Check(_ => throw new HttpRequestException("no route"));

        Assert.False(result.IsNewer);
        Assert.Contains("1.6.0", result.Message);
        Assert.Null(result.DownloadUrl);
    }

    [Fact]
    public async Task A_rate_limit_says_to_try_again_later()
    {
        var result = await Check(_ => Json("{}", HttpStatusCode.Forbidden));

        Assert.Contains("wait", result.Message);
        Assert.False(result.IsNewer);
    }

    [Fact]
    public async Task Nonsense_from_github_is_not_an_update()
    {
        var result = await Check(_ => Json("not json at all"));

        Assert.False(result.IsNewer);
        Assert.Contains("1.6.0", result.Message);
    }

    // GitHub answers 403 to a request that does not name itself.
    [Fact]
    public async Task The_request_names_the_app()
    {
        var handler = new FakeHandler(_ => Json(Release("v1.6.0")));
        await Check(handler);

        Assert.Contains("QuadStickConfigManager",
            string.Join(" ", handler.Last!.Headers.GetValues("User-Agent")));
    }

    [Theory]
    [InlineData("1.7.0", "1.6.0", 1)]
    [InlineData("1.6.0", "1.7.0", -1)]
    [InlineData("1.6", "1.6.0", 0)]
    [InlineData("1.10.0", "1.9.0", 1)]   // not a string compare
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("nightly", "1.6.0", 0)]  // not a version, so not newer
    public void Versions_compare_by_number(string a, string b, int expected)
    {
        Assert.Equal(expected, UpdateCheck.Compare(a, b));
    }
}
