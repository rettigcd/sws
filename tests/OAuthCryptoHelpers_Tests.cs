namespace sws.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Automation;
using Shouldly;
using Xunit;

public class OAuthCryptoHelpers_Tests {

	static readonly Regex Base64UrlPattern = new("^[A-Za-z0-9_-]+$");

	[Fact]
	public void GenerateState_ProducesUrlSafeValue_AndVariesPerCall() {
		var first = OAuthCryptoHelpers.GenerateState();
		var second = OAuthCryptoHelpers.GenerateState();

		Base64UrlPattern.IsMatch(first).ShouldBeTrue();
		first.ShouldNotBe(second);
	}

	[Fact]
	public void GenerateNonce_ProducesUrlSafeValue_AndVariesPerCall() {
		var first = OAuthCryptoHelpers.GenerateNonce();
		var second = OAuthCryptoHelpers.GenerateNonce();

		Base64UrlPattern.IsMatch(first).ShouldBeTrue();
		first.ShouldNotBe(second);
	}

	[Fact]
	public void GenerateCodeVerifier_MeetsRfc7636LengthAndCharsetRequirements() {
		var verifier = OAuthCryptoHelpers.GenerateCodeVerifier();

		verifier.Length.ShouldBeInRange(43, 128);
		Base64UrlPattern.IsMatch(verifier).ShouldBeTrue();
	}

	[Fact]
	public void DeriveCodeChallengeS256_IsDeterministic_AndMatchesIndependentSha256Computation() {
		var verifier = OAuthCryptoHelpers.GenerateCodeVerifier();

		var challenge = OAuthCryptoHelpers.DeriveCodeChallengeS256(verifier);

		var expectedHash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
		var expectedChallenge = Convert.ToBase64String(expectedHash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		challenge.ShouldBe(expectedChallenge);
	}

	[Fact]
	public void Base64UrlEncode_ContainsNoPaddingOrUnsafeCharacters() {
		var encoded = OAuthCryptoHelpers.Base64UrlEncode([0xFB, 0xEF, 0xFF]);

		encoded.ShouldNotContain("=");
		encoded.ShouldNotContain("+");
		encoded.ShouldNotContain("/");
	}
}
