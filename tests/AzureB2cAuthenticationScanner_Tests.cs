namespace sws.Tests;

using System.Text.Json;
using Shouldly;
using Xunit;

public class AzureB2cAuthenticationScanner_Tests {
	[Fact]
	public void Scan_SummarizesOauthB2cRequestTypes() {
		// Given
		var sessions = new List<Session> {
			BuildSession(
				2,
				"GET",
				"https://tqlidentitystage.b2clogin.com/tqlidentitystage.onmicrosoft.com/b2c_1a_signup_signin_passwordreset/oauth2/v2.0/authorize?client_id=abc&response_type=code&code_challenge=xyz&nonce=123",
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
					["client_id"] = "abc",
					["response_type"] = "code",
					["code_challenge"] = "xyz",
					["nonce"] = "123",
				},
				formBody: null,
				responseJson: null
			),
			BuildSession(
				3,
				"POST",
				"https://tqlidentitystage.b2clogin.com/tqlidentitystage.onmicrosoft.com/b2c_1a_signup_signin_passwordreset/oauth2/v2.0/token",
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
				formBody: new List<FormBodyEntry> {
					new("grant_type", "authorization_code"),
					new("code", "auth-code"),
					new("code_verifier", "some-code-verifier"),
				},
				responseJson: JsonDocument.Parse("""
					{
						"access_token": "abc",
						"refresh_token": "def",
						"expires_in": 3600
					}
				""").RootElement.Clone()
			)
		};

		// When
		var report = AzureB2cAuthenticationScanner.Scan(sessions);

		// Then
		report.Requests.Count.ShouldBe(2);
		report.Requests[0].SessionId.ShouldBe(2);
		report.Requests[0].RequestType.ShouldBe(RequestType.AuthorizationRequest_AuthCodeWithPKCE);
		report.Requests[1].SessionId.ShouldBe(3);
		report.Requests[1].RequestType.ShouldBe(RequestType.AuthorizationCodeTokenRequest);
	}

	[Fact]
	public void Scan_ExcludesNonOauthB2cSessions() {
		// Given
		var sessions = new List<Session> {
			BuildSession(
				2,
				"GET",
				"https://tqlidentitystage.b2clogin.com/tqlidentitystage.onmicrosoft.com/b2c_1a_signup_signin_passwordreset/oauth2/v2.0/authorize?client_id=abc&response_type=code&code_challenge=xyz&nonce=123",
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
					["client_id"] = "abc",
					["response_type"] = "code",
					["code_challenge"] = "xyz",
					["nonce"] = "123",
				},
				formBody: null,
				responseJson: JsonDocument.Parse("""
					{
						"access_token": "unexpected-on-authorize"
					}
				""").RootElement.Clone()
			),
			BuildSession(
				4,
				"GET",
				"https://example.com/home",
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
				formBody: null,
				responseJson: null
			)
		};

		// When
		var report = AzureB2cAuthenticationScanner.Scan(sessions);

		// Then
		report.Requests.Count.ShouldBe(1);
		report.Requests[0].SessionId.ShouldBe(2);
		report.Requests[0].RequestType.ShouldBe(RequestType.AuthorizationRequest_AuthCodeWithPKCE);
	}

	[Fact]
	public void Classify_ReturnsAuthorizationCallbackRequest_ForCodeAndStateQuery() {
		// Given
		var session = BuildSession(
			7,
			"GET",
			"https://app.example.com/signin-oidc?code=abc123&state=st123",
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["code"] = "abc123",
				["state"] = "st123",
			},
			formBody: null,
			responseJson: null
		);

		// When
		var sessionType = AzureB2cAuthenticationScanner.ClassifySession(session, []);

		// Then
		sessionType.ShouldBe(RequestType.AuthorizationCallbackRequest);
	}

	[Fact]
	public void Classify_ReturnsAuthorizationCodeTokenRequest_ForAuthorizationCodeGrant() {
		// Given
		var session = BuildSession(
			8,
			"POST",
			"https://tqlidentitystage.b2clogin.com/tqlidentitystage.onmicrosoft.com/b2c_1a_signup_signin_passwordreset/oauth2/v2.0/token",
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "auth-code"),
				new("code_verifier", "verifier"),
			},
			responseJson: null
		);

		// When
		var sessionType = AzureB2cAuthenticationScanner.ClassifySession(session, []);

		// Then
		sessionType.ShouldBe(RequestType.AuthorizationCodeTokenRequest);
	}

	[Fact]
	public void Classify_ReturnsRefreshTokenRequest_ForRefreshTokenGrant() {
		// Given
		var session = BuildSession(
			9,
			"POST",
			"https://tqlidentitystage.b2clogin.com/tqlidentitystage.onmicrosoft.com/b2c_1a_signup_signin_passwordreset/oauth2/v2.0/token",
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			formBody: new List<FormBodyEntry> {
				new("grant_type", "refresh_token"),
				new("refresh_token", "refresh-token-value"),
			},
			responseJson: null
		);

		// When
		var sessionType = AzureB2cAuthenticationScanner.ClassifySession(session, []);

		// Then
		sessionType.ShouldBe(RequestType.RefreshTokenRequest);
	}

	[Fact]
	public void Classify_ReturnsAuthorizationCallbackRequest_ForFragmentModeUsingPriorAuthorizeSession() {
		// Given
		var authorizeSession = BuildSession(
			10,
			"GET",
			"https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signup_signin/oauth2/v2.0/authorize?client_id=abc&response_type=code&redirect_uri=https%3A%2F%2Fapp.example.com%2Fauthcallback&response_mode=fragment",
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["client_id"] = "abc",
				["response_type"] = "code",
				["redirect_uri"] = "https://app.example.com/authcallback",
				["response_mode"] = "fragment",
			},
			formBody: null,
			responseJson: null,
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["Location"] = "https://app.example.com/authcallback#code=fragment-code&state=fragment-state",
			}
		);

		var callbackSession = BuildSession(
			11,
			"GET",
			"https://app.example.com/authcallback",
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			formBody: null,
			responseJson: null
		);

		// When
		var sessionType = AzureB2cAuthenticationScanner.ClassifySession(callbackSession, [authorizeSession]);

		// Then
		sessionType.ShouldBe(RequestType.AuthorizationCallbackRequest);
	}

	[Fact]
	public void Request_DefaultRequestType_IsUnknown() {
		// Given
		var session = BuildSession(
			12,
			"GET",
			"https://example.com/home",
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			formBody: null,
			responseJson: null
		);

		// When
		var requestType = session.Request.RequestType;

		// Then
		requestType.ShouldBe(RequestType.Unknown);
	}

	[Fact]
	public void ClassifySession_ReturnsExistingRequestType_WithoutReclassification() {
		// Given
		var session = BuildSession(
			13,
			"GET",
			"https://example.com/unrelated",
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			formBody: null,
			responseJson: null
		) with {
			Request = BuildSession(
				13,
				"GET",
				"https://example.com/unrelated",
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
				formBody: null,
				responseJson: null
			).Request with {
				RequestType = RequestType.RefreshTokenRequest,
			}
		};

		// When
		var requestType = AzureB2cAuthenticationScanner.ClassifySession(session, []);

		// Then
		requestType.ShouldBe(RequestType.RefreshTokenRequest);
	}

	static Session BuildSession(
		int sessionId,
		string method,
		string url,
		Dictionary<string, string> query,
		List<FormBodyEntry>? formBody,
		JsonElement? responseJson,
		Dictionary<string, string>? responseHeaders = null
	) {
		var request = new Request(
			$"{method} {url} HTTP/1.1",
			method,
			url,
			"HTTP/1.1",
			url,
			new Uri(url).Host,
			query,
			null,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			new List<string>(),
			new Body(0, null, "none", new List<string>()),
			null,
			formBody,
			new List<string>()
		);

		var response = new Response(
			"HTTP/1.1 200 OK",
			200,
			"OK",
			responseHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			new Body(0, null, "none", new List<string>()),
			null,
			responseJson,
			new List<string>()
		);

		return new Session(sessionId, null, request, response);
	}
}