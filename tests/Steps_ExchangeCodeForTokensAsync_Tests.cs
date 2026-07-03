namespace sws.Tests;

using System.Net;
using System.Web;
using Minimal;
using Shouldly;
using Xunit;

public class Steps_ExchangeCodeForTokensAsync_Tests {

	const string TokenEndpoint = "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/token";
	const string RedirectUri = "https://app.example.com/callback";

	static Context BuildContext(FakeAuthHttpClient http) {
		return new Context {
			Http = http,
			WellKnown = new WellKnownConfiguration { TokenEndpoint = TokenEndpoint },
			ClientId = "client-1",
			RedirectUri = RedirectUri,
			Code = "captured-code",
			CodeVerifier = "captured-verifier",
		};
	}

	static string ExtractFormValue(string formEncodedBody, string name) {
		var pairs = formEncodedBody.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
		return pairs.GetValueOrDefault(name, "");
	}

	[Fact]
	public async Task ExchangeCodeForTokensAsync_PostsExpectedFormFields_AndPopulatesTokens() {
		var fakeHttpClient = new FakeAuthHttpClient();
		string? postedBody = null;

		fakeHttpClient.Enqueue(HttpMethod.Post, TokenEndpoint, request => {
			postedBody = request.Content!.ReadAsStringAsync().Result;
			return FakeResponses.Json("""
				{ "access_token": "final-access-token", "token_type": "Bearer", "expires_in": 3600, "id_token": "final-id-token", "refresh_token": "final-refresh-token", "scope": "openid profile" }
				""");
		});

		var ctx = BuildContext(fakeHttpClient);

		var result = await Steps.ExchangeCodeForTokensAsync(ctx);

		result.ShouldBe(StepResult.TokensReceived);
		ctx.Tokens.ShouldNotBeNull();
		ctx.Tokens.AccessToken.ShouldBe("final-access-token");
		ctx.Tokens.TokenType.ShouldBe("Bearer");
		ctx.Tokens.ExpiresIn.ShouldBe(3600);
		ctx.Tokens.IdToken.ShouldBe("final-id-token");
		ctx.Tokens.RefreshToken.ShouldBe("final-refresh-token");
		ctx.Tokens.Scope.ShouldBe("openid profile");

		postedBody.ShouldNotBeNull();
		ExtractFormValue(postedBody, "grant_type").ShouldBe("authorization_code");
		ExtractFormValue(postedBody, "code").ShouldBe("captured-code");
		ExtractFormValue(postedBody, "redirect_uri").ShouldBe(RedirectUri);
		ExtractFormValue(postedBody, "client_id").ShouldBe("client-1");
		ExtractFormValue(postedBody, "code_verifier").ShouldBe("captured-verifier");
	}

	[Fact]
	public async Task ExchangeCodeForTokensAsync_Throws_WithErrorDescription_WhenTokenEndpointRejectsRequest() {
		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Post, TokenEndpoint, _ =>
			FakeResponses.Json("""{ "error": "invalid_grant", "error_description": "The authorization code has expired." }""", HttpStatusCode.BadRequest)
		);

		var ctx = BuildContext(fakeHttpClient);

		var ex = await Should.ThrowAsync<InvalidOperationException>(() => Steps.ExchangeCodeForTokensAsync(ctx));
		ex.Message.ShouldContain("The authorization code has expired.");
		ctx.Tokens.ShouldBeNull();
	}

	[Fact]
	public async Task ExchangeCodeForTokensAsync_Throws_WhenResponseHasNoAccessToken() {
		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Post, TokenEndpoint, _ =>
			FakeResponses.Json("""{ "token_type": "Bearer" }""")
		);

		var ctx = BuildContext(fakeHttpClient);

		await Should.ThrowAsync<InvalidOperationException>(() => Steps.ExchangeCodeForTokensAsync(ctx));
		ctx.Tokens.ShouldBeNull();
	}

	[Fact]
	public async Task ExchangeCodeForTokensAsync_Throws_WhenCodeIsMissing() {
		var fakeHttpClient = new FakeAuthHttpClient();
		var ctx = BuildContext(fakeHttpClient);
		ctx.Code = null;

		await Should.ThrowAsync<InvalidOperationException>(() => Steps.ExchangeCodeForTokensAsync(ctx));
		fakeHttpClient.SentRequests.ShouldBeEmpty();
	}

	[Fact]
	public async Task ExchangeCodeForTokensAsync_Throws_WhenCodeVerifierIsMissing() {
		var fakeHttpClient = new FakeAuthHttpClient();
		var ctx = BuildContext(fakeHttpClient);
		ctx.CodeVerifier = null;

		await Should.ThrowAsync<InvalidOperationException>(() => Steps.ExchangeCodeForTokensAsync(ctx));
		fakeHttpClient.SentRequests.ShouldBeEmpty();
	}
}
