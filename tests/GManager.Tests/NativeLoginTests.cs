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

    [Fact]
    public void ParseProfileSupportsMagiskPifJson()
    {
        var magiskPif = """
        {
          "PRODUCT": "komodo",
          "DEVICE": "komodo",
          "MANUFACTURER": "Google",
          "BRAND": "google",
          "MODEL": "Pixel 9 Pro XL",
          "FINGERPRINT": "google/komodo/komodo:14/AD1A.240905.004/12185678:user/release-keys",
          "SECURITY_PATCH": "2024-09-05",
          "FIRST_API_LEVEL": "34"
        }
        """;
        var profile = NativeDevicesWindow.ParseProfile(magiskPif, "pif.json");
        Assert.Equal("Pixel 9 Pro XL", profile.Model);
        Assert.Equal("google", profile.Brand);
        Assert.Equal("Google", profile.Manufacturer);
        Assert.Equal("komodo", profile.Device);
        Assert.Equal(34, profile.SdkVersion);
        Assert.Contains("release-keys", profile.Fingerprint);
        profile.Validate();
    }

    [Fact]
    public void PixelNineProImportDerivesCaimanInsteadOfKomodo()
    {
        const string json = """
        {"MODEL":"Pixel 9 Pro","FINGERPRINT":"google/caiman/caiman:14/AD1A.240905.004/example:user/release-keys"}
        """;
        var profile = NativeDevicesWindow.ParseProfile(json, "pif.json");
        Assert.Equal("caiman", profile.Product);
        Assert.Equal("caiman", profile.Device);
        profile.Validate();
    }

    [Fact]
    public void PifLaunchApiDoesNotOverrideCurrentAndroidRelease()
    {
        const string json = """
        {"MODEL":"Pixel 9 Pro","FIRST_API_LEVEL":"34","FINGERPRINT":"google/caiman/caiman:16/BP2A.fixture/example:user/release-keys"}
        """;
        var profile = NativeDevicesWindow.ParseProfile(json, "pif.json");
        Assert.Equal(36, profile.SdkVersion);
        Assert.Equal("16", profile.AndroidRelease);
        Assert.Equal("BP2A.fixture", profile.BuildId);
    }

    [Fact]
    public void PixelNineProRejectsKomodoIdentity()
    {
        const string json = """
        {"MODEL":"Pixel 9 Pro","PRODUCT":"komodo","DEVICE":"komodo","FINGERPRINT":"google/komodo/komodo:14/AD1A.240905.004/example:user/release-keys"}
        """;
        var profile = NativeDevicesWindow.ParseProfile(json, "pif.json");
        Assert.Throws<ArgumentException>(profile.Validate);
    }
}
