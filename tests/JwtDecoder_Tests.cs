namespace sws.Tests;

using Automation;
using Shouldly;
using Xunit;

public class JwtDecoder_Tests {

	static string BuildUnsignedJwt(string payloadJson) {
		var header = Automation.OAuthCryptoHelpers.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
		var payload = Automation.OAuthCryptoHelpers.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(payloadJson));
		return $"{header}.{payload}.";
	}

	[Fact]
	public void Decode_ExtractsScalarClaims() {
		var token = BuildUnsignedJwt("""{"sub":"user-1","email":"user@example.com","exp":1999999999}""");

		var claims = JwtDecoder.Decode(token);

		claims.ShouldContain(c => c.Name == "sub" && c.Value == "user-1");
		claims.ShouldContain(c => c.Name == "email" && c.Value == "user@example.com");
		claims.ShouldContain(c => c.Name == "exp" && c.Value == "1999999999");
	}

	[Fact]
	public void Decode_ExpandsArrayClaims_IntoOneEntryPerValue() {
		var token = BuildUnsignedJwt("""{"roles":["admin","user"]}""");

		var claims = JwtDecoder.Decode(token);

		claims.Count(c => c.Name == "roles").ShouldBe(2);
		claims.ShouldContain(c => c.Name == "roles" && c.Value == "admin");
		claims.ShouldContain(c => c.Name == "roles" && c.Value == "user");
	}

	[Fact]
	public void Decode_ReturnsEmptyList_ForOpaqueNonJwtToken() {
		var claims = JwtDecoder.Decode("opaque-access-token-12345");

		claims.ShouldBeEmpty();
	}

	[Fact]
	public void Decode_ReturnsEmptyList_ForNullOrEmptyToken() {
		JwtDecoder.Decode(null).ShouldBeEmpty();
		JwtDecoder.Decode("").ShouldBeEmpty();
	}
}
