namespace sws.Tests;

using System.Text.Json;
using Auth;
using Shouldly;
using Xunit;
using static sws.Tests.TestSessionBuilder;

public class SessionClassifier_Tests {

	[Fact]
	public void ClassifyUnknownSessions_ClassifiesAuthCodeWithPkceAndTokenRequest() {
		// Given
		var sessions = new List<Session> {
			BuildSession(
				2,
				"GET",
				"https://tqlidentitystage.b2clogin.com/tqlidentitystage.onmicrosoft.com/b2c_1a_signup_signin_passwordreset/oauth2/v2.0/authorize?client_id=abc&response_type=code&code_challenge=xyz&nonce=123"
			),
			BuildSession(
				3,
				"POST",
				"https://tqlidentitystage.b2clogin.com/tqlidentitystage.onmicrosoft.com/b2c_1a_signup_signin_passwordreset/oauth2/v2.0/token",
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
		var classified = SessionClassifier.ClassifyUnknownSessions(sessions);

		// Then
		classified[0].Request.RequestType.ShouldBe(RequestType.AuthorizationRequest_AuthCodeWithPKCE);
		classified[0].Response.ResponseClassification.ShouldBe(ResponseType.SuccessResponse);
		classified[1].Request.RequestType.ShouldBe(RequestType.AuthorizationCodeTokenRequest);
		classified[1].Response.ResponseClassification.ShouldBe(ResponseType.TokenResponse);
	}

	[Fact]
	public void Classify_ReturnsAuthorizationCallbackRequest_ForCodeAndStateQuery() {
		// Given
		var session = BuildSession(
			7,
			"GET",
			"https://app.example.com/signin-oidc?code=abc123&state=st123"
		);

		// When
		var sessionType = SessionClassifier.ClassifySession(session, []);

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
			formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "auth-code"),
				new("code_verifier", "verifier"),
			}
		);

		// When
		var sessionType = SessionClassifier.ClassifySession(session, []);

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
			formBody: new List<FormBodyEntry> {
				new("grant_type", "refresh_token"),
				new("refresh_token", "refresh-token-value"),
			}
		);

		// When
		var sessionType = SessionClassifier.ClassifySession(session, []);

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
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["Location"] = "https://app.example.com/authcallback#code=fragment-code&state=fragment-state",
			}
		);

		var callbackSession = BuildSession(11, "GET", "https://app.example.com/authcallback");

		// When
		var sessionType = SessionClassifier.ClassifySession(callbackSession, [authorizeSession]);

		// Then
		sessionType.ShouldBe(RequestType.AuthorizationCallbackRequest);
	}

	[Fact]
	public void Request_DefaultRequestType_IsUnknown() {
		// Given
		var session = BuildSession(12, "GET", "https://example.com/home");

		// When
		var requestType = session.Request.RequestType;

		// Then
		requestType.ShouldBe(RequestType.Unknown);
	}

	[Fact]
	public void ClassifySession_ReturnsExistingRequestType_WithoutReclassification() {
		// Given
		var session = BuildSession(13, "GET", "https://example.com/unrelated") with {
			Request = BuildSession(13, "GET", "https://example.com/unrelated").Request with {
				RequestType = RequestType.RefreshTokenRequest,
			}
		};

		// When
		var requestType = SessionClassifier.ClassifySession(session, []);

		// Then
		requestType.ShouldBe(RequestType.RefreshTokenRequest);
	}

	[Fact]
	public void ClassifyUnknownSessions_ClassifiesTokenResponse() {
		// Given
		var session = BuildSession(
			20,
			"POST",
			"https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/token",
			formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "auth-code"),
			},
			responseJson: JsonDocument.Parse("""
				{
					"access_token": "token123",
					"refresh_token": "refresh123",
					"expires_in": 3600
				}
			""").RootElement.Clone()
		);

		// When
		var classifiedSessions = SessionClassifier.ClassifyUnknownSessions([session]);

		// Then
		classifiedSessions[0].Response.ResponseClassification.ShouldBe(ResponseType.TokenResponse);
	}

	[Fact]
	public void ClassifyUnknownSessions_ClassifiesAuthorizationRedirect() {
		// Given
		var session = BuildSession(
			21,
			"GET",
			"https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize?client_id=abc&response_type=code",
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["Location"] = "https://app.example.com/callback?code=auth-code&state=state-123",
			},
			statusCode: 302
		);

		// When
		var classifiedSessions = SessionClassifier.ClassifyUnknownSessions([session]);

		// Then
		classifiedSessions[0].Response.ResponseClassification.ShouldBe(ResponseType.AuthorizationRedirect);
	}

	[Fact]
	public void ClassifyUnknownSessions_ClassifiesErrorResponse() {
		// Given
		var session = BuildSession(
			22,
			"POST",
			"https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/token",
			formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "invalid-code"),
			},
			responseJson: JsonDocument.Parse("""
				{
					"error": "invalid_grant",
					"error_description": "The provided authorization code is invalid."
				}
			""").RootElement.Clone(),
			statusCode: 400
		);

		// When
		var classifiedSessions = SessionClassifier.ClassifyUnknownSessions([session]);

		// Then
		classifiedSessions[0].Response.ResponseClassification.ShouldBe(ResponseType.ErrorResponse);
	}

	[Fact]
	public void ClassifyUnknownSessions_ClassifiesOpenIdConfigurationResponse() {
		// Given
		var session = BuildSession(
			23,
			"GET",
			"https://tenant.b2clogin.com/tenant.onmicrosoft.com/.well-known/openid-configuration",
			responseJson: JsonDocument.Parse("""
				{
					"authorization_endpoint": "https://tenant.b2clogin.com/authorize",
					"token_endpoint": "https://tenant.b2clogin.com/token",
					"issuer": "https://tenant.b2clogin.com/"
				}
			""").RootElement.Clone()
		);

		// When
		var classifiedSessions = SessionClassifier.ClassifyUnknownSessions([session]);

		// Then
		classifiedSessions[0].Response.ResponseClassification.ShouldBe(ResponseType.ConfigurationResponse);
	}

	[Fact]
	public void Classify_DetectsGenericNonAzureOidcProvider() {
		// Given: a non-Azure OIDC provider using /connect/authorize and /connect/token, no oauth2/v2.0 anywhere.
		var authorizeSession = BuildSession(
			30,
			"GET",
			"https://login.example.com/connect/authorize?client_id=abc&response_type=code&code_challenge=xyz"
		);

		var tokenSession = BuildSession(
			31,
			"POST",
			"https://login.example.com/connect/token",
			formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "auth-code"),
				new("code_verifier", "verifier"),
			}
		);

		// When
		var authorizeType = SessionClassifier.ClassifySession(authorizeSession, []);
		var tokenType = SessionClassifier.ClassifySession(tokenSession, []);

		// Then
		authorizeType.ShouldBe(RequestType.AuthorizationRequest_AuthCodeWithPKCE);
		tokenType.ShouldBe(RequestType.AuthorizationCodeTokenRequest);
	}

	[Fact]
	public void Classify_ReturnsClientCredentialsTokenRequest_ForClientCredentialsGrant() {
		// Given
		var session = BuildSession(
			40,
			"POST",
			"https://login.example.com/connect/token",
			formBody: new List<FormBodyEntry> {
				new("grant_type", "client_credentials"),
				new("client_id", "service-client"),
				new("client_secret", "shh"),
			}
		);

		// When
		var sessionType = SessionClassifier.ClassifySession(session, []);

		// Then
		sessionType.ShouldBe(RequestType.ClientCredentialsTokenRequest);
	}

	[Fact]
	public void Classify_ReturnsPasswordTokenRequest_ForPasswordGrant() {
		// Given
		var session = BuildSession(
			41,
			"POST",
			"https://login.example.com/connect/token",
			formBody: new List<FormBodyEntry> {
				new("grant_type", "password"),
				new("username", "user@example.com"),
				new("password", "secret"),
			}
		);

		// When
		var sessionType = SessionClassifier.ClassifySession(session, []);

		// Then
		sessionType.ShouldBe(RequestType.PasswordTokenRequest);
	}

	[Fact]
	public void Classify_ReturnsDeviceCodeTokenRequest_ForDeviceCodeGrant() {
		// Given
		var session = BuildSession(
			42,
			"POST",
			"https://login.example.com/connect/token",
			formBody: new List<FormBodyEntry> {
				new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
				new("device_code", "device-code-value"),
			}
		);

		// When
		var sessionType = SessionClassifier.ClassifySession(session, []);

		// Then
		sessionType.ShouldBe(RequestType.DeviceCodeTokenRequest);
	}
}
