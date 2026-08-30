using System.Net;
using QuadStick.App;
using Xunit;

namespace QuadStick.App.Tests;

public class GoogleAuthConcurrencyTests
{
    sealed class MemoryTokenStore : ITokenStore
    {
        string? _token = "refresh-token";
        public string? Load() => _token;
        public void Save(string refreshToken) => _token = refreshToken;
        public void Delete() => _token = null;
    }

    sealed class TokenHandler : HttpMessageHandler
    {
        int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            // Keep the first refresh in flight long enough for every caller to
            // reach the refresh gate; without single-flight this count climbs.
            await Task.Delay(40, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"access\",\"expires_in\":3600}"),
            };
        }
    }

    [Fact]
    public async Task Concurrent_stale_callers_refresh_only_once()
    {
        var handler = new TokenHandler();
        var auth = new GoogleAuth(new MemoryTokenStore(), handler);

        var tokens = await Task.WhenAll(
            Enumerable.Range(0, 24).Select(_ => auth.GetAccessTokenAsync()));

        Assert.All(tokens, token => Assert.Equal("access", token));
        Assert.Equal(1, handler.Calls);
    }
}
