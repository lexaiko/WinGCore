using System.Text.RegularExpressions;
using GManager.Auth;
using Xunit;

namespace GManager.Tests;

public class PkceGeneratorTests
{
    [Fact]
    public void GenerateCodeVerifier_DefaultLength_Is64()
    {
        var verifier = PkceGenerator.GenerateCodeVerifier();
        Assert.Equal(64, verifier.Length);
    }

    [Theory]
    [InlineData(43)]
    [InlineData(64)]
    [InlineData(96)]
    [InlineData(128)]
    public void GenerateCodeVerifier_ValidLengths_Succeeds(int length)
    {
        var verifier = PkceGenerator.GenerateCodeVerifier(length);
        Assert.Equal(length, verifier.Length);
        Assert.Matches("^[A-Za-z0-9_\\.\\-\\~]+$", verifier);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(129)]
    [InlineData(0)]
    [InlineData(-10)]
    public void GenerateCodeVerifier_InvalidLengths_ThrowsArgumentOutOfRangeException(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PkceGenerator.GenerateCodeVerifier(length));
    }

    [Fact]
    public void GenerateCodeChallenge_RFC7636_TestVector_MatchesExpectedChallenge()
    {
        // RFC 7636 Appendix B Test Vector
        // https://datatracker.ietf.org/doc/html/rfc7636#appendix-B
        const string rfcVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expectedChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        var actualChallenge = PkceGenerator.GenerateCodeChallenge(rfcVerifier);

        Assert.Equal(expectedChallenge, actualChallenge);
    }

    [Fact]
    public void CreatePkcePair_GeneratesValidMatchingPair()
    {
        var (verifier, challenge, method) = PkceGenerator.CreatePkcePair(64);

        Assert.Equal(64, verifier.Length);
        Assert.NotEmpty(challenge);
        Assert.Equal("S256", method);

        // Verification of the generated pair
        var recalculatedChallenge = PkceGenerator.GenerateCodeChallenge(verifier);
        Assert.Equal(recalculatedChallenge, challenge);
    }
}
