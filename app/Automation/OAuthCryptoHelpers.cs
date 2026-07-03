using System.Security.Cryptography;
using System.Text;

namespace Automation;

/// <summary>
/// Generates fresh per-run OAuth2/OIDC runtime values. Never reuses captured values -
/// the whole point of the automation engine is to mint new state/nonce/PKCE material per run.
/// </summary>
internal static class OAuthCryptoHelpers {

	public static string GenerateState() {
		return Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
	}

	public static string GenerateNonce() {
		return Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
	}

	/// <summary>RFC 7636 code_verifier: 43-128 chars from the unreserved character set.</summary>
	public static string GenerateCodeVerifier() {
		return Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
	}

	public static string DeriveCodeChallengeS256(string codeVerifier) {
		return Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
	}

	public static string Base64UrlEncode(byte[] bytes) {
		return Convert.ToBase64String(bytes)
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}
}
