namespace sws.Tests;

using Saz;
using System.Text.Json;
using Auth;
using Shouldly;
using Xunit;
using static sws.Tests.TestExchangeBuilder;

public class ExchangeClassifier_Tests {

	[Fact]
	public void Classify_ClassifiesAuthCodeWithPkceAndTokenRequest() {
		// Given
		var exchanges = new List<Exchange> {
			BuildExchange(
				2,
				"GET",
				"https://tqlidentitystage.b2clogin.com/tqlidentitystage.onmicrosoft.com/b2c_1a_signup_signin_passwordreset/oauth2/v2.0/authorize?client_id=abc&response_type=code&code_challenge=xyz&nonce=123"
			),
			BuildExchange(
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
		var classified = ExchangeClassifier.Classify(exchanges);

		// Then
		classified[0].RequestType.ShouldBe(RequestType.AuthorizationRequest_AuthCodeWithPKCE);
		classified[0].ResponseType.ShouldBe(ResponseType.SuccessResponse);
		classified[1].RequestType.ShouldBe(RequestType.AuthorizationCodeTokenRequest);
		classified[1].ResponseType.ShouldBe(ResponseType.TokenResponse);
	}

	[Fact]
	public void Classify_ReturnsAuthorizationCallbackRequest_ForCodeAndStateQuery() {
		// Given
		var exchange = BuildExchange(
			7,
			"GET",
			"https://app.example.com/signin-oidc?code=abc123&state=st123"
		);

		// When
		var exchangeType = ExchangeClassifier.ClassifyRequest(exchange, []);

		// Then
		exchangeType.ShouldBe(RequestType.AuthorizationCallbackRequest);
	}

	[Fact]
	public void Classify_ReturnsAuthorizationCodeTokenRequest_ForAuthorizationCodeGrant() {
		// Given
		var exchange = BuildExchange(
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
		var exchangeType = ExchangeClassifier.ClassifyRequest(exchange, []);

		// Then
		exchangeType.ShouldBe(RequestType.AuthorizationCodeTokenRequest);
	}

	[Fact]
	public void Classify_ReturnsRefreshTokenRequest_ForRefreshTokenGrant() {
		// Given
		var exchange = BuildExchange(
			9,
			"POST",
			"https://tqlidentitystage.b2clogin.com/tqlidentitystage.onmicrosoft.com/b2c_1a_signup_signin_passwordreset/oauth2/v2.0/token",
			formBody: new List<FormBodyEntry> {
				new("grant_type", "refresh_token"),
				new("refresh_token", "refresh-token-value"),
			}
		);

		// When
		var exchangeType = ExchangeClassifier.ClassifyRequest(exchange, []);

		// Then
		exchangeType.ShouldBe(RequestType.RefreshTokenRequest);
	}

	[Fact]
	public void Classify_ReturnsAuthorizationCallbackRequest_ForFragmentModeUsingPriorAuthorizeExchange() {
		// Given
		var authorizeExchange = BuildExchange(
			10,
			"GET",
			"https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signup_signin/oauth2/v2.0/authorize?client_id=abc&response_type=code&redirect_uri=https%3A%2F%2Fapp.example.com%2Fauthcallback&response_mode=fragment",
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["Location"] = "https://app.example.com/authcallback#code=fragment-code&state=fragment-state",
			}
		);

		var callbackExchange = BuildExchange(11, "GET", "https://app.example.com/authcallback");

		// When
		var exchangeType = ExchangeClassifier.ClassifyRequest(callbackExchange, [authorizeExchange]);

		// Then
		exchangeType.ShouldBe(RequestType.AuthorizationCallbackRequest);
	}

	[Fact]
	public void Classify_ClassifiesTokenResponse() {
		// Given
		var exchange = BuildExchange(
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
		var classifiedExchanges = ExchangeClassifier.Classify([exchange]);

		// Then
		classifiedExchanges[0].ResponseType.ShouldBe(ResponseType.TokenResponse);
	}

	[Fact]
	public void Classify_ClassifiesAuthorizationRedirect() {
		// Given
		var exchange = BuildExchange(
			21,
			"GET",
			"https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize?client_id=abc&response_type=code",
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["Location"] = "https://app.example.com/callback?code=auth-code&state=state-123",
			},
			statusCode: 302
		);

		// When
		var classifiedExchanges = ExchangeClassifier.Classify([exchange]);

		// Then
		classifiedExchanges[0].ResponseType.ShouldBe(ResponseType.AuthorizationRedirect);
	}

	[Fact]
	public void Classify_ClassifiesErrorResponse() {
		// Given
		var exchange = BuildExchange(
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
		var classifiedExchanges = ExchangeClassifier.Classify([exchange]);

		// Then
		classifiedExchanges[0].ResponseType.ShouldBe(ResponseType.ErrorResponse);
	}

	[Fact]
	public void Classify_ClassifiesOpenIdConfigurationResponse() {
		// Given
		var exchange = BuildExchange(
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
		var classifiedExchanges = ExchangeClassifier.Classify([exchange]);

		// Then
		classifiedExchanges[0].ResponseType.ShouldBe(ResponseType.ConfigurationResponse);
	}

	[Fact]
	public void Classify_DetectsGenericNonAzureOidcProvider() {
		// Given: a non-Azure OIDC provider using /connect/authorize and /connect/token, no oauth2/v2.0 anywhere.
		var authorizeExchange = BuildExchange(
			30,
			"GET",
			"https://login.example.com/connect/authorize?client_id=abc&response_type=code&code_challenge=xyz"
		);

		var tokenExchange = BuildExchange(
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
		var authorizeType = ExchangeClassifier.ClassifyRequest(authorizeExchange, []);
		var tokenType = ExchangeClassifier.ClassifyRequest(tokenExchange, []);

		// Then
		authorizeType.ShouldBe(RequestType.AuthorizationRequest_AuthCodeWithPKCE);
		tokenType.ShouldBe(RequestType.AuthorizationCodeTokenRequest);
	}

	[Fact]
	public void Classify_ReturnsClientCredentialsTokenRequest_ForClientCredentialsGrant() {
		// Given
		var exchange = BuildExchange(
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
		var exchangeType = ExchangeClassifier.ClassifyRequest(exchange, []);

		// Then
		exchangeType.ShouldBe(RequestType.ClientCredentialsTokenRequest);
	}

	[Fact]
	public void Classify_ReturnsPasswordTokenRequest_ForPasswordGrant() {
		// Given
		var exchange = BuildExchange(
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
		var exchangeType = ExchangeClassifier.ClassifyRequest(exchange, []);

		// Then
		exchangeType.ShouldBe(RequestType.PasswordTokenRequest);
	}

	[Fact]
	public void Classify_ReturnsDeviceCodeTokenRequest_ForDeviceCodeGrant() {
		// Given
		var exchange = BuildExchange(
			42,
			"POST",
			"https://login.example.com/connect/token",
			formBody: new List<FormBodyEntry> {
				new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
				new("device_code", "device-code-value"),
			}
		);

		// When
		var exchangeType = ExchangeClassifier.ClassifyRequest(exchange, []);

		// Then
		exchangeType.ShouldBe(RequestType.DeviceCodeTokenRequest);
	}
}
