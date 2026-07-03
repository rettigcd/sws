namespace sws.Tests;

using System.Web;
using Minimal;
using Shouldly;
using Xunit;

public class Steps_SendAuthorizationRequestAsync_Tests {

	const string AuthorizationEndpoint = "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize";
	const string RedirectUri = "https://app.example.com/callback";

	static Context BuildContext(FakeAuthHttpClient http) {
		return new Context {
			Http = http,
			WellKnown = new WellKnownConfiguration { AuthorizationEndpoint = AuthorizationEndpoint },
			ClientId = "client-1",
			RedirectUri = RedirectUri,
			Scope = "openid profile",
		};
	}

	static string ExtractQueryValue(Uri uri, string name) {
		return HttpUtility.ParseQueryString(uri.Query)[name] ?? "";
	}

	[Fact]
	public async Task SendAuthorizationRequestAsync_BuildsAuthCodePkceRequest_WithFreshStateAndPkceValues() {
		var fakeHttpClient = new FakeAuthHttpClient();
		string? sentState = null;
		string? sentCodeChallenge = null;

		fakeHttpClient.Enqueue(HttpMethod.Get, AuthorizationEndpoint, request => {
			sentState = ExtractQueryValue(request.RequestUri!, "state");
			sentCodeChallenge = ExtractQueryValue(request.RequestUri!, "code_challenge");
			return FakeResponses.Redirect($"{RedirectUri}?code=query-code&state={sentState}");
		});

		var ctx = BuildContext(fakeHttpClient);

		var result = await Steps.SendAuthorizationRequestAsync(ctx);

		result.ShouldBe(StepResult.RedirectBackToClient);
		ctx.Code.ShouldBe("query-code");

		var sentRequest = fakeHttpClient.SentRequests.Single();
		ExtractQueryValue(sentRequest.RequestUri!, "response_type").ShouldBe("code");
		ExtractQueryValue(sentRequest.RequestUri!, "client_id").ShouldBe("client-1");
		ExtractQueryValue(sentRequest.RequestUri!, "redirect_uri").ShouldBe(RedirectUri);
		ExtractQueryValue(sentRequest.RequestUri!, "scope").ShouldBe("openid profile");
		ExtractQueryValue(sentRequest.RequestUri!, "code_challenge_method").ShouldBe("S256");

		sentState.ShouldNotBeNullOrEmpty();
		sentCodeChallenge.ShouldNotBeNullOrEmpty();
		ctx.State.ShouldBe(sentState);
		ctx.CodeChallenge.ShouldBe(sentCodeChallenge);
		ctx.CodeVerifier.ShouldNotBeNullOrEmpty();
	}

	[Fact]
	public async Task SendAuthorizationRequestAsync_ExtractsCode_FromRedirectFragment() {
		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, AuthorizationEndpoint, request => {
			var state = ExtractQueryValue(request.RequestUri!, "state");
			return FakeResponses.Redirect($"{RedirectUri}#code=fragment-code&state={state}");
		});

		var ctx = BuildContext(fakeHttpClient);

		var result = await Steps.SendAuthorizationRequestAsync(ctx);

		result.ShouldBe(StepResult.RedirectBackToClient);
		ctx.Code.ShouldBe("fragment-code");
	}

	[Fact]
	public async Task SendAuthorizationRequestAsync_ExtractsCode_FromFormPostHiddenInputs() {
		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, AuthorizationEndpoint, request => {
			var state = ExtractQueryValue(request.RequestUri!, "state");
			return FakeResponses.Html($"""
				<html><body>
				<form method="POST" action="https://app.example.com/callback">
					<input type="hidden" name="code" value="form-post-code" />
					<input type="hidden" name="state" value="{state}" />
				</form>
				</body></html>
				""");
		});

		var ctx = BuildContext(fakeHttpClient);

		var result = await Steps.SendAuthorizationRequestAsync(ctx);

		result.ShouldBe(StepResult.RedirectBackToClient);
		ctx.Code.ShouldBe("form-post-code");
	}

	[Fact]
	public async Task SendAuthorizationRequestAsync_ReturnsLoginPage_WhenHtmlHasNoHiddenCodeInput() {
		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, AuthorizationEndpoint, _ => FakeResponses.Html("""
			<html><body>
			<form>
				<input type="email" name="Username" />
				<input type="password" name="Password" />
			</form>
			</body></html>
			"""));

		var ctx = BuildContext(fakeHttpClient);

		var result = await Steps.SendAuthorizationRequestAsync(ctx);

		result.ShouldBe(StepResult.LoginPage);
		ctx.Code.ShouldBeNull();
	}

	[Fact]
	public async Task SendAuthorizationRequestAsync_Throws_WhenRedirectCarriesError() {
		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, AuthorizationEndpoint, _ =>
			FakeResponses.Redirect($"{RedirectUri}?error=access_denied&error_description=The+user+declined.")
		);

		var ctx = BuildContext(fakeHttpClient);

		var ex = await Should.ThrowAsync<InvalidOperationException>(() => Steps.SendAuthorizationRequestAsync(ctx));
		ex.Message.ShouldContain("The user declined.");
	}

	[Fact]
	public async Task SendAuthorizationRequestAsync_Throws_WhenReturnedStateDoesNotMatch() {
		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, AuthorizationEndpoint, _ =>
			FakeResponses.Redirect($"{RedirectUri}?code=some-code&state=attacker-state")
		);

		var ctx = BuildContext(fakeHttpClient);

		await Should.ThrowAsync<InvalidOperationException>(() => Steps.SendAuthorizationRequestAsync(ctx));
		ctx.Code.ShouldBeNull();
	}
}
