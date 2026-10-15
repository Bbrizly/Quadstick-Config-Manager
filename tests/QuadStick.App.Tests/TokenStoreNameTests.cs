using Xunit;

namespace QuadStick.App.Tests;

// A host's Google sign-in is its own. The free app's has to stay exactly where
// it always was, or every existing user is signed out on update.
public class TokenStoreNameTests
{
    [Fact]
    public void The_free_app_keeps_the_sign_in_it_always_had()
    {
        Assert.Equal("QuadStick Config Manager", TokenStore.DefaultName);
        Assert.Equal(TokenStore.DefaultName, TokenStore.Name);
        var tail = Path.Combine("QuadStickConfigManager", "google-drive.token");
        Assert.EndsWith(tail, TokenStore.WindowsPath, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_name_moves_the_sign_in_somewhere_else()
    {
        var was = TokenStore.Name;
        try
        {
            var free = TokenStore.WindowsPath;
            TokenStore.Name = "Some Host";
            Assert.Equal("Some Host", TokenStore.Name);
            Assert.NotEqual(free, TokenStore.WindowsPath);
            Assert.EndsWith(Path.Combine("SomeHost", "google-drive.token"), TokenStore.WindowsPath, StringComparison.Ordinal);
        }
        finally { TokenStore.Name = was; }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void A_name_that_is_not_a_plain_name_is_refused(string bad)
    {
        Assert.Throws<ArgumentException>(() => TokenStore.Name = bad);
        Assert.Equal(TokenStore.DefaultName, TokenStore.Name);
    }
}
