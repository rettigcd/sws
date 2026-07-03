namespace sws.Tests;

using System.Net;
using Minimal;
using Shouldly;
using Xunit;

public class Steps_LoadWellKnownConfigurationAsync_Tests {

	[Fact]
	public async Task LoadWellKnownConfigurationAsync_PopulatesContext_WhenEndpointReturnsValidDocument() {
		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/v2.0/.well-known/openid-configuration", _ =>
			FakeResponses.Json("""
				{
					"issuer": "https://tenant.b2clogin.com/tenant-id/v2.0/",
					"authorization_endpoint": "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize",
					"token_endpoint": "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/token",
					"end_session_endpoint": "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/logout",
					"jwks_uri": "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/discovery/v2.0/keys"
				}
				""")
		);

		var ctx = new Context {
			Http = fakeHttpClient,
			WellKnownEndpoint = "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/v2.0/.well-known/openid-configuration",
			ClientId = "client-1",
			RedirectUri = "https://app.example.com/callback",
			Scope = "openid profile",
		};

		var result = await Steps.LoadWellKnownConfigurationAsync(ctx);

		result.ShouldBe(StepResult.WellKnownConfigRetrieved);
		ctx.WellKnown.ShouldNotBeNull();
		ctx.WellKnown.Issuer.ShouldBe("https://tenant.b2clogin.com/tenant-id/v2.0/");
		ctx.WellKnown.AuthorizationEndpoint.ShouldBe("https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize");
		ctx.WellKnown.TokenEndpoint.ShouldBe("https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/token");
		ctx.WellKnown.EndSessionEndpoint.ShouldBe("https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/logout");
		ctx.WellKnown.JwksUri.ShouldBe("https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/discovery/v2.0/keys");
	}

	[Fact]
	public async Task LoadWellKnownConfigurationAsync_Throws_WhenEndpointReturnsNonSuccessStatusCode() {
		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, "https://tenant.b2clogin.com/missing/.well-known/openid-configuration", _ =>
			FakeResponses.Json("""{ "error": "not found" }""", HttpStatusCode.NotFound)
		);

		var ctx = new Context {
			Http = fakeHttpClient,
			WellKnownEndpoint = "https://tenant.b2clogin.com/missing/.well-known/openid-configuration",
			ClientId = "client-1",
			RedirectUri = "https://app.example.com/callback",
			Scope = "openid profile",
		};

		await Should.ThrowAsync<InvalidOperationException>(() => Steps.LoadWellKnownConfigurationAsync(ctx));
		ctx.WellKnown.ShouldBeNull();
	}
}
