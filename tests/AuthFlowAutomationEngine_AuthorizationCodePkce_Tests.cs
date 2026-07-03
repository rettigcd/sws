namespace sws.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Auth;
using Automation;
using Shouldly;
using Xunit;
using static sws.Tests.TestSessionBuilder;

public class AuthFlowAutomationEngine_AuthorizationCodePkce_Tests {

	/// <summary>
	/// A minimal 3-session capture: authorize -> callback (carrying the login form's
	/// username/password, since that's what a B2C self-asserted page's redirect ultimately
	/// resolves to) -> token exchange. Mirrors the shape AuthFlowDetector_Tests uses for
	/// UsernamePasswordCredentials detection, just with a token session added.
	/// </summary>
	static List<Session> BuildCapturedB2cFlowSessions() {
		return [
			BuildSession(
				1, "GET",
				"https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize"
				+ "?client_id=client-1&response_type=code&code_challenge=captured-challenge&code_challenge_method=S256"
				+ "&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcallback&scope=openid%20profile&state=captured-state"
			),
			BuildSession(
				2, "POST", "https://app.example.com/callback?code=captured-code&state=captured-state",
				formBody: new List<FormBodyEntry> {
					new("username", "captured-user@example.com"),
					new("password", "captured-password"),
				}
			),
			BuildSession(3, "POST", "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "captured-code"),
				new("code_verifier", "captured-verifier"),
			}),
		];
	}

	/// <summary>Authorize + callback (both carrying the B2C SSO cookie) + token, no login form observed.</summary>
	static List<Session> BuildSsoSufficientSessions(bool includeTokenSession) {
		var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-cpim-sso"] = "sso-cookie-value" };
		var sessions = new List<Session> {
			BuildSession(
				1, "GET",
				"https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize?client_id=client-1&response_type=code&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcallback&state=captured-state",
				cookies: cookies
			),
			BuildSession(2, "GET", "https://app.example.com/callback?code=captured-code&state=captured-state", cookies: cookies),
		};

		if (includeTokenSession) {
			sessions.Add(BuildSession(3, "POST", "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "captured-code"),
			}));
		}

		return sessions;
	}

	static DetectedAuthenticationFlow DetectPkceFlow(List<Session> sessions) {
		var flow = AuthFlowDetector.Detect(sessions).Flows.Single();
		flow.FlowType.ShouldBe(AuthFlowType.AuthorizationCodeWithPkce);
		flow.AuthenticationMethod.ShouldBeOfType<UsernamePasswordCredentials>();
		return flow;
	}

	static string ExtractQueryValue(Uri uri, string name) {
		return HttpUtility.ParseQueryString(uri.Query)[name] ?? "";
	}

	static string ExtractFormValue(string formEncodedBody, string name) {
		var pairs = formEncodedBody.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
		return pairs.GetValueOrDefault(name, "");
	}

	[Fact]
	public async Task ExecuteAsync_CompletesFullLoginFormFlow_WithFreshPkceValues_AndReturnsTokensAndClaims() {
		var sessions = BuildCapturedB2cFlowSessions();
		var flow = DetectPkceFlow(sessions);

		string? generatedState = null;
		string? generatedCodeChallenge = null;
		string? postedUsername = null;
		string? postedPassword = null;
		string? echoedHiddenField = null;

		var fakeHttpClient = new FakeAuthHttpClient();

		fakeHttpClient.Enqueue(HttpMethod.Get, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize", request => {
			generatedState = ExtractQueryValue(request.RequestUri!, "state");
			generatedCodeChallenge = ExtractQueryValue(request.RequestUri!, "code_challenge");
			generatedCodeChallenge.ShouldNotBe("captured-challenge");

			var html = """
				<html><body>
				<form method="POST" action="/tenant.onmicrosoft.com/b2c_1a_signin/login/submit">
					<input type="hidden" name="__RequestVerificationToken" value="csrf-token-xyz" />
					<input type="email" name="Username" />
					<input type="password" name="Password" />
				</form>
				</body></html>
				""";
			return FakeResponses.Html(html);
		});

		fakeHttpClient.Enqueue(HttpMethod.Post, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/login/submit", request => {
			var body = request.Content!.ReadAsStringAsync().Result;
			postedUsername = ExtractFormValue(body, "Username");
			postedPassword = ExtractFormValue(body, "Password");
			echoedHiddenField = ExtractFormValue(body, "__RequestVerificationToken");

			return FakeResponses.Redirect($"https://app.example.com/callback?code=fresh-auth-code&state={generatedState}");
		});

		fakeHttpClient.Enqueue(HttpMethod.Post, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/token", request => {
			var body = request.Content!.ReadAsStringAsync().Result;
			body.ShouldContain("code=fresh-auth-code");

			var postedVerifier = ExtractFormValue(body, "code_verifier");
			var expectedChallenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(postedVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
			expectedChallenge.ShouldBe(generatedCodeChallenge);

			var idToken = BuildUnsignedJwt("""{"sub":"user-123","email":"captured-user@example.com"}""");
			return FakeResponses.Json($$"""
				{ "access_token": "final-access-token", "id_token": "{{idToken}}", "refresh_token": "final-refresh-token", "expires_in": 3600 }
			""");
		});

		var result = await AuthFlowAutomationEngine.ExecuteAsync(flow, sessions, new AutomationOptions(HttpClient: fakeHttpClient));

		result.Success.ShouldBeTrue(result.ErrorMessage ?? result.UnsupportedReason?.Message);
		result.Tokens!.AccessToken.ShouldBe("final-access-token");
		result.Tokens.RefreshToken.ShouldBe("final-refresh-token");
		result.Claims.ShouldContain(c => c.Name == "email" && c.Value == "captured-user@example.com");

		postedUsername.ShouldBe("captured-user@example.com");
		postedPassword.ShouldBe("captured-password");
		echoedHiddenField.ShouldBe("csrf-token-xyz");
		generatedState.ShouldNotBe("captured-state");

		result.Steps.ShouldNotBeEmpty();
		foreach (var step in result.Steps) {
			step.Description.ShouldNotContain("captured-password");
			step.Description.ShouldNotContain("final-access-token");
			step.Description.ShouldNotContain("final-refresh-token");
		}
	}

	[Fact]
	public async Task ExecuteAsync_SkipsLoginForm_WhenSessionCookieAloneCompletesAuthorization() {
		var sessions = BuildSsoSufficientSessions(includeTokenSession: true);
		var flow = AuthFlowDetector.Detect(sessions).Flows.Single(f => f.FlowType == AuthFlowType.AuthorizationCode);
		flow.AuthenticationMethod.ShouldBeOfType<SessionCookieCredentials>();

		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize", request => {
			var state = ExtractQueryValue(request.RequestUri!, "state");
			return FakeResponses.Redirect($"https://app.example.com/callback?code=sso-auth-code&state={state}");
		});
		fakeHttpClient.Enqueue(HttpMethod.Post, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/token", request =>
			FakeResponses.Json("""{ "access_token": "sso-access-token" }""")
		);

		var result = await AuthFlowAutomationEngine.ExecuteAsync(flow, sessions, new AutomationOptions(HttpClient: fakeHttpClient));

		result.Success.ShouldBeTrue(result.ErrorMessage ?? result.UnsupportedReason?.Message);
		result.Steps.ShouldNotContain(s => s.Description.Contains("Submitted login form"));
		fakeHttpClient.SentRequests.Count.ShouldBe(2);
	}

	[Fact]
	public async Task ExecuteAsync_ReturnsMissingCredentials_WhenSsoExpectedButCookieDoesNotWorkThisRun() {
		var sessions = BuildSsoSufficientSessions(includeTokenSession: true);
		var flow = AuthFlowDetector.Detect(sessions).Flows.Single(f => f.FlowType == AuthFlowType.AuthorizationCode);
		flow.AuthenticationMethod.ShouldBeOfType<SessionCookieCredentials>();

		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize", request =>
			FakeResponses.Html("<html><body><form><input type='password' name='Password'/></form></body></html>")
		);

		var result = await AuthFlowAutomationEngine.ExecuteAsync(flow, sessions, new AutomationOptions(HttpClient: fakeHttpClient));

		result.Success.ShouldBeFalse();
		result.UnsupportedReason!.Kind.ShouldBe(UnsupportedFlowReasonKind.MissingCredentials);
	}

	[Fact]
	public async Task ExecuteAsync_ReturnsFailure_WhenLoginFormIsReDisplayedAfterSubmission() {
		var sessions = BuildCapturedB2cFlowSessions();
		var flow = DetectPkceFlow(sessions);

		var fakeHttpClient = new FakeAuthHttpClient();
		var loginHtml = """
			<html><body>
			<form method="POST" action="/tenant.onmicrosoft.com/b2c_1a_signin/login/submit">
				<input type="email" name="Username" />
				<input type="password" name="Password" />
			</form>
			</body></html>
			""";
		fakeHttpClient.Enqueue(HttpMethod.Get, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize", _ => FakeResponses.Html(loginHtml));
		fakeHttpClient.Enqueue(HttpMethod.Post, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/login/submit", _ => FakeResponses.Html(loginHtml));

		var result = await AuthFlowAutomationEngine.ExecuteAsync(flow, sessions, new AutomationOptions(HttpClient: fakeHttpClient));

		result.Success.ShouldBeFalse();
		result.UnsupportedReason.ShouldBeNull();
		result.ErrorMessage.ShouldNotBeNull();
	}

	[Fact]
	public async Task ExecuteAsync_ReturnsMfaRequired_WhenLoginPageHasExtraRequiredField() {
		var sessions = BuildCapturedB2cFlowSessions();
		var flow = DetectPkceFlow(sessions);

		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize", _ => FakeResponses.Html("""
			<html><body>
			<form>
				<input type="email" name="Username" />
				<input type="password" name="Password" />
				<input type="text" name="otpCode" required />
			</form>
			</body></html>
			"""));

		var result = await AuthFlowAutomationEngine.ExecuteAsync(flow, sessions, new AutomationOptions(HttpClient: fakeHttpClient));

		result.Success.ShouldBeFalse();
		result.UnsupportedReason!.Kind.ShouldBe(UnsupportedFlowReasonKind.MfaRequired);
	}

	[Fact]
	public async Task ExecuteAsync_ReturnsCaptchaRequired_WhenLoginPageHasCaptchaWidget() {
		var sessions = BuildCapturedB2cFlowSessions();
		var flow = DetectPkceFlow(sessions);

		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize", _ => FakeResponses.Html("""
			<html><body>
			<form><div class="g-recaptcha"></div><input type="password" name="Password" /></form>
			</body></html>
			"""));

		var result = await AuthFlowAutomationEngine.ExecuteAsync(flow, sessions, new AutomationOptions(HttpClient: fakeHttpClient));

		result.Success.ShouldBeFalse();
		result.UnsupportedReason!.Kind.ShouldBe(UnsupportedFlowReasonKind.CaptchaRequired);
	}

	[Fact]
	public async Task ExecuteAsync_ReturnsFailure_WhenReturnedStateDoesNotMatchSentState() {
		var sessions = BuildSsoSufficientSessions(includeTokenSession: true);
		var flow = AuthFlowDetector.Detect(sessions).Flows.Single(f => f.FlowType == AuthFlowType.AuthorizationCode);

		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Get, "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize", _ =>
			FakeResponses.Redirect("https://app.example.com/callback?code=sso-auth-code&state=attacker-controlled-state")
		);

		var result = await AuthFlowAutomationEngine.ExecuteAsync(flow, sessions, new AutomationOptions(HttpClient: fakeHttpClient));

		result.Success.ShouldBeFalse();
		result.Tokens.ShouldBeNull();
		fakeHttpClient.SentRequests.Count.ShouldBe(1);
	}

	static string BuildUnsignedJwt(string payloadJson) {
		var header = OAuthCryptoHelpers.Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
		var payload = OAuthCryptoHelpers.Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
		return $"{header}.{payload}.";
	}
}
