using GManager.Views;

namespace GManager.Tests;

public class NativeLoginTests
{
    [Theory]
    [InlineData("https://accounts.google.com/v3/signin/speedbump/embeddedsigninconsent#close", true)]
    [InlineData("https://accounts.google.com/o/oauth2/programmatic_auth?result=fixture", true)]
    [InlineData("https://accounts.google.com/signin/continue#close", true)]
    [InlineData("https://accounts.google.com/signin/continue", false)]
    [InlineData("https://accounts.google.com/v3/signin/speedbump/embeddedsigninconsent", false)]
    [InlineData("https://accounts.google.com/v3/signin/challenge/pwd", false)]
    [InlineData("https://accounts.google.com/EmbeddedSetup?next=%23close", false)]
    [InlineData("https://accounts.google.com/o/oauth2/programmatic_auth-untrusted", false)]
    [InlineData("https://evil.test/#close", false)]
    [InlineData("http://accounts.google.com/#close", false)]
    public void CompletionRequiresGoogleOriginAndExactTerminalMarker(string uri, bool expected) =>
        Assert.Equal(expected, NativeLoginWindow.IsLoginCompletionUri(uri));

    [Theory]
    [InlineData("https://accounts.google.com/EmbeddedSetup", true)]
    [InlineData("https://accounts.google.com:443/identifier", true)]
    [InlineData("http://accounts.google.com/", false)]
    [InlineData("https://accounts.google.com.evil.test/", false)]
    [InlineData("https://accounts.google.com@evil.test/", false)]
    [InlineData("https://user@accounts.google.com/", false)]
    [InlineData("https://accounts.google.com:444/", false)]
    [InlineData("file:///C:/test.html", false)]
    public void BrowserBridgeRequiresExactGoogleOrigin(string uri, bool expected) =>
        Assert.Equal(expected, NativeLoginWindow.IsGoogleLoginOrigin(uri));
}
