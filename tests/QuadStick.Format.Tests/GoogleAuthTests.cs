using System.Net;
using QuadStick.App;
using Xunit;

namespace QuadStick.Format.Tests;

public class GoogleAuthTests
{
    // Fake token endpoint. Records the last request body.
    class TokenHandler : HttpMessageHandler
    {
        readonly Func<string, HttpResponseMessage> _responder;
        public string? LastBody;

        public TokenHandler(Func<string, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            return _responder(LastBody);
        }
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public void S256_MatchesRfc7636Vector()
    {
        // RFC 7636 appendix B.
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            GoogleAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public async Task StateMismatch_IsRejected()
    {
        var (listener, port) = GoogleAuth.StartLoopback();
        try
        {
            var task = GoogleAuth.AwaitCodeAsync(listener, "goodstate", CancellationToken.None);
            using var http = new HttpClient();
            await http.GetAsync($"http://127.0.0.1:{port}/?state=badstate&code=xyz");
            await Assert.ThrowsAsync<GoogleAuthException>(() => task);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GoodState_ReturnsCode()
    {
        var (listener, port) = GoogleAuth.StartLoopback();
        try
        {
            var task = GoogleAuth.AwaitCodeAsync(listener, "goodstate", CancellationToken.None);
            using var http = new HttpClient();
            await http.GetAsync($"http://127.0.0.1:{port}/?state=goodstate&code=the-code");
            Assert.Equal("the-code", await task);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task TokenExchange_ParsesAndStoresRefreshToken()
    {
        var store = new InMemoryTokenStore();
        var handler = new TokenHandler(_ => Json("{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"expires_in\":3600}"));
        var auth = new GoogleAuth(store, handler);

        await auth.ExchangeCodeAsync("code", "verifier", "http://127.0.0.1:1/", CancellationToken.None);

        Assert.Equal("RT", store.Load());
        Assert.Contains("grant_type=authorization_code", handler.LastBody);
        Assert.Equal("AT", await auth.GetAccessTokenAsync()); // cached, no second call
    }

    [Fact]
    public async Task Refresh_RenewsAccessToken()
    {
        var store = new InMemoryTokenStore();
        store.Save("RT");
        var handler = new TokenHandler(_ => Json("{\"access_token\":\"AT2\",\"expires_in\":3600}"));
        var auth = new GoogleAuth(store, handler);

        Assert.Equal("AT2", await auth.GetAccessTokenAsync());
        Assert.Contains("grant_type=refresh_token", handler.LastBody);
    }

    [Fact]
    public async Task InvalidGrant_ThrowsRevoked()
    {
        var store = new InMemoryTokenStore();
        store.Save("RT");
        var handler = new TokenHandler(_ => Json("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest));
        var auth = new GoogleAuth(store, handler);

        await Assert.ThrowsAsync<GoogleAuthRevokedException>(() => auth.GetAccessTokenAsync());
    }

    [Fact]
    public void InMemoryTokenStore_RoundTrips()
    {
        var store = new InMemoryTokenStore();
        Assert.Null(store.Load());
        store.Save("tok");
        Assert.Equal("tok", store.Load());
        store.Delete();
        Assert.Null(store.Load());
    }
}
