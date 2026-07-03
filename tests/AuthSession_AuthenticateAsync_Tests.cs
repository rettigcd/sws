namespace sws.Tests;

using System.Web;
using Minimal;
using Shouldly;
using Xunit;

public class AuthSession_AuthenticateAsync_Tests {

	const string WellKnownEndpoint = "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/v2.0/.well-known/openid-configuration";
	const string AuthorizationEndpoint = "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize";
	const string TokenEndpoint = "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/token";
	const string RedirectUri = "https://app.example.com/callback";

	static string ExtractQueryValue(Uri uri, string name) {
		return HttpUtility.ParseQueryString(uri.Query)[name] ?? "";
	}

	static string ExtractFormValue(string formEncodedBody, string name) {
		var pairs = formEncodedBody.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
		return pairs.GetValueOrDefault(name, "");
	}

	static void EnqueueWellKnown(FakeAuthHttpClient http) {
		http.Enqueue(HttpMethod.Get, WellKnownEndpoint, _ => FakeResponses.Json($$"""
			{ "issuer": "https://tenant.b2clogin.com/tenant-id/v2.0/", "authorization_endpoint": "{{AuthorizationEndpoint}}", "token_endpoint": "{{TokenEndpoint}}" }
			"""));
	}

	static void EnqueueImmediateRedirect(FakeAuthHttpClient http, string code) {
		http.Enqueue(HttpMethod.Get, AuthorizationEndpoint, request => {
			var state = ExtractQueryValue(request.RequestUri!, "state");
			return FakeResponses.Redirect($"{RedirectUri}?code={code}&state={state}");
		});
	}

	static void EnqueueTokenResponse(FakeAuthHttpClient http, string accessToken) {
		http.Enqueue(HttpMethod.Post, TokenEndpoint, _ => FakeResponses.Json($$"""
			{ "access_token": "{{accessToken}}", "token_type": "Bearer" }
			"""));
	}

	[Fact]
	public async Task AuthenticateAsync_CompletesFullFlow_WhenAuthorizationEndpointRedirectsImmediately() {
		var fakeHttpClient = new FakeAuthHttpClient();
		EnqueueWellKnown(fakeHttpClient);
		EnqueueImmediateRedirect(fakeHttpClient, code: "sso-auth-code");
		EnqueueTokenResponse(fakeHttpClient, accessToken: "final-access-token");

		var session = new AuthSession(fakeHttpClient);
		session.InitAuthorizationRequest(WellKnownEndpoint, clientId: "client-1", redirectUri: RedirectUri, scope: "openid profile");

		await session.AuthenticateAsync();

		fakeHttpClient.SentRequests.Count.ShouldBe(3);
		fakeHttpClient.SentRequests[0].RequestUri!.GetLeftPart(UriPartial.Path).ShouldBe(WellKnownEndpoint);
		fakeHttpClient.SentRequests[1].RequestUri!.GetLeftPart(UriPartial.Path).ShouldBe(AuthorizationEndpoint);
		fakeHttpClient.SentRequests[2].RequestUri!.GetLeftPart(UriPartial.Path).ShouldBe(TokenEndpoint);

		var tokenRequestBody = await fakeHttpClient.SentRequests[2].Content!.ReadAsStringAsync();
		ExtractFormValue(tokenRequestBody, "grant_type").ShouldBe("authorization_code");
		ExtractFormValue(tokenRequestBody, "code").ShouldBe("sso-auth-code");
		ExtractFormValue(tokenRequestBody, "redirect_uri").ShouldBe(RedirectUri);
		ExtractFormValue(tokenRequestBody, "client_id").ShouldBe("client-1");
		ExtractFormValue(tokenRequestBody, "code_verifier").ShouldNotBeNullOrEmpty();

		session.Tokens.ShouldNotBeNull();
		session.Tokens.AccessToken.ShouldBe("final-access-token");
	}

	[Fact]
	public async Task AuthenticateAsync_DoesNotRefetchWellKnownConfig_OnSecondCall() {
		var fakeHttpClient = new FakeAuthHttpClient();
		EnqueueWellKnown(fakeHttpClient);
		EnqueueImmediateRedirect(fakeHttpClient, code: "first-code");
		EnqueueTokenResponse(fakeHttpClient, accessToken: "first-access-token");
		EnqueueImmediateRedirect(fakeHttpClient, code: "second-code");
		EnqueueTokenResponse(fakeHttpClient, accessToken: "second-access-token");

		var session = new AuthSession(fakeHttpClient);
		session.InitAuthorizationRequest(WellKnownEndpoint, clientId: "client-1", redirectUri: RedirectUri, scope: "openid profile");

		await session.AuthenticateAsync();
		await session.AuthenticateAsync();

		fakeHttpClient.SentRequests.Count(r => r.RequestUri!.GetLeftPart(UriPartial.Path) == WellKnownEndpoint).ShouldBe(1);
		fakeHttpClient.SentRequests.Count(r => r.RequestUri!.GetLeftPart(UriPartial.Path) == AuthorizationEndpoint).ShouldBe(2);
		fakeHttpClient.SentRequests.Count(r => r.RequestUri!.GetLeftPart(UriPartial.Path) == TokenEndpoint).ShouldBe(2);
	}

	[Fact]
	public async Task AuthenticateAsync_CompletesFullFlow_WhenAuthorizationEndpointPresentsALoginForm() {
		var fakeHttpClient = new FakeAuthHttpClient();
		const string loginSubmitUrl = "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/login/submit";

		string? sentState = null;
		EnqueueWellKnown(fakeHttpClient);
		fakeHttpClient.Enqueue(HttpMethod.Get, AuthorizationEndpoint, request => {
			sentState = ExtractQueryValue(request.RequestUri!, "state");
			return FakeResponses.Html($"""
				<html><body>
				<form method="POST" action="{loginSubmitUrl}">
					<input type="hidden" name="__RequestVerificationToken" value="csrf-token-xyz" />
					<input type="email" name="Username" />
					<input type="password" name="Password" />
				</form>
				</body></html>
				""");
		});

		string? postedUsername = null;
		string? postedPassword = null;
		string? echoedHiddenField = null;
		fakeHttpClient.Enqueue(HttpMethod.Post, loginSubmitUrl, request => {
			var body = request.Content!.ReadAsStringAsync().Result;
			postedUsername = ExtractFormValue(body, "Username");
			postedPassword = ExtractFormValue(body, "Password");
			echoedHiddenField = ExtractFormValue(body, "__RequestVerificationToken");

			return FakeResponses.Redirect($"{RedirectUri}?code=login-form-code&state={sentState}");
		});
		EnqueueTokenResponse(fakeHttpClient, accessToken: "final-access-token");

		var session = new AuthSession(fakeHttpClient);
		session.InitAuthorizationRequest(WellKnownEndpoint, clientId: "client-1", redirectUri: RedirectUri, scope: "openid profile");
		session.InitAuthorization(username: "user@example.com", password: "hunter2");

		await session.AuthenticateAsync();

		fakeHttpClient.SentRequests.Count.ShouldBe(4);
		postedUsername.ShouldBe("user@example.com");
		postedPassword.ShouldBe("hunter2");
		echoedHiddenField.ShouldBe("csrf-token-xyz");

		var tokenRequestBody = await fakeHttpClient.SentRequests[3].Content!.ReadAsStringAsync();
		ExtractFormValue(tokenRequestBody, "code").ShouldBe("login-form-code");

		session.Tokens.ShouldNotBeNull();
		session.Tokens.AccessToken.ShouldBe("final-access-token");
	}

	[Fact]
	public async Task AuthenticateAsync_Throws_WhenLoginFormIsReDisplayedAfterSubmission() {
		var fakeHttpClient = new FakeAuthHttpClient();
		const string loginSubmitUrl = "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/login/submit";
		var loginHtml = $"""
			<html><body>
			<form method="POST" action="{loginSubmitUrl}">
				<input type="email" name="Username" />
				<input type="password" name="Password" />
			</form>
			</body></html>
			""";

		EnqueueWellKnown(fakeHttpClient);
		fakeHttpClient.Enqueue(HttpMethod.Get, AuthorizationEndpoint, _ => FakeResponses.Html(loginHtml));
		fakeHttpClient.Enqueue(HttpMethod.Post, loginSubmitUrl, _ => FakeResponses.Html(loginHtml));

		var session = new AuthSession(fakeHttpClient);
		session.InitAuthorizationRequest(WellKnownEndpoint, clientId: "client-1", redirectUri: RedirectUri, scope: "openid profile");
		session.InitAuthorization(username: "user@example.com", password: "wrong-password");

		var ex = await Should.ThrowAsync<InvalidOperationException>(() => session.AuthenticateAsync());
		ex.Message.ShouldContain("did not complete authentication");
		fakeHttpClient.SentRequests.Count.ShouldBe(3);
	}

	[Fact]
	public async Task AuthenticateAsync_Throws_WhenLoginPageIsPresented_AndCredentialsWereNeverProvided() {
		var fakeHttpClient = new FakeAuthHttpClient();
		EnqueueWellKnown(fakeHttpClient);
		fakeHttpClient.Enqueue(HttpMethod.Get, AuthorizationEndpoint, _ => FakeResponses.Html("""
			<html><body>
			<form>
				<input type="email" name="Username" />
				<input type="password" name="Password" />
			</form>
			</body></html>
			"""));

		var session = new AuthSession(fakeHttpClient);
		session.InitAuthorizationRequest(WellKnownEndpoint, clientId: "client-1", redirectUri: RedirectUri, scope: "openid profile");

		var ex = await Should.ThrowAsync<InvalidOperationException>(() => session.AuthenticateAsync());
		ex.Message.ShouldContain("Context.Username is not set");
	}
}
