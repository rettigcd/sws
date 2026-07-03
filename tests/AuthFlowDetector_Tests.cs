namespace sws.Tests;

using System.Text.Json;
using Auth;
using Shouldly;
using Xunit;
using static sws.Tests.TestSessionBuilder;

public class AuthFlowDetector_Tests {

	[Fact]
	public void Detect_CorrelatesAuthCodePkceFlow_ByStateMatch() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/authorize?client_id=abc&response_type=code&code_challenge=xyz&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcallback&state=st-1"),
			BuildSession(2, "GET", "https://app.example.com/callback?code=auth-code-1&state=st-1"),
			BuildSession(3, "POST", "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/oauth2/v2.0/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "auth-code-1"),
				new("code_verifier", "verifier-1"),
			}),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows.Count.ShouldBe(1);
		var flow = result.Flows[0];
		flow.FlowType.ShouldBe(AuthFlowType.AuthorizationCodeWithPkce);
		flow.Confidence.ShouldBe(1.0);
		flow.AuthorizationRequestSessionId.ShouldBe(1);
		flow.AuthorizationCallbackSessionId.ShouldBe(2);
		flow.TokenRequestSessionId.ShouldBe(3);
		flow.RelatedSessionIds.ShouldBe([1, 2, 3]);
	}

	[Fact]
	public void Detect_UsesDiscoveryDocumentEndpoints_ForNonStandardPaths() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://idp.example.com/.well-known/openid-configuration", responseJson: JsonDocument.Parse("""
				{
					"issuer": "https://idp.example.com/",
					"authorization_endpoint": "https://idp.example.com/custom/auth",
					"token_endpoint": "https://idp.example.com/custom/tok"
				}
			""").RootElement.Clone()),
			BuildSession(2, "GET", "https://idp.example.com/custom/auth?response_type=code&client_id=abc&code_challenge=xyz&state=st-42"),
			BuildSession(3, "GET", "https://app.example.com/callback?code=auth-code-1&state=st-42"),
			BuildSession(4, "POST", "https://idp.example.com/custom/tok", formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "auth-code-1"),
				new("code_verifier", "verifier-1"),
			}),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows.Count.ShouldBe(1);
		var flow = result.Flows[0];
		flow.Discovery.ShouldNotBeNull();
		flow.Discovery!.TokenEndpoint.ShouldBe("https://idp.example.com/custom/tok");
		flow.TokenRequestSessionId.ShouldBe(4);
		flow.FlowType.ShouldBe(AuthFlowType.AuthorizationCodeWithPkce);
	}

	[Fact]
	public void Detect_CreatesStandaloneClientCredentialsFlow() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "POST", "https://login.example.com/connect/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "client_credentials"),
				new("client_id", "service-client"),
				new("client_secret", "shh"),
			}),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows.Count.ShouldBe(1);
		var flow = result.Flows[0];
		flow.FlowType.ShouldBe(AuthFlowType.ClientCredentials);
		flow.Confidence.ShouldBe(1.0);
		flow.Warnings.ShouldNotContain(w => w.Kind == FlowWarningKind.MissingClientSecret);
	}

	[Fact]
	public void Detect_FlagsMissingClientSecret_ForClientCredentialsWithoutSecret() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "POST", "https://login.example.com/connect/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "client_credentials"),
				new("client_id", "service-client"),
			}),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows[0].Warnings.ShouldContain(w => w.Kind == FlowWarningKind.MissingClientSecret);
	}

	[Fact]
	public void Detect_LinksRefreshTokenRequest_ToOriginatingFlow() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://login.example.com/connect/authorize?client_id=abc&response_type=code&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcallback&state=st-1"),
			BuildSession(2, "GET", "https://app.example.com/callback?code=auth-code-1&state=st-1"),
			BuildSession(3, "POST", "https://login.example.com/connect/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "auth-code-1"),
			}, responseJson: JsonDocument.Parse("""
				{ "access_token": "at-1", "refresh_token": "rt-1" }
			""").RootElement.Clone()),
			BuildSession(4, "POST", "https://login.example.com/connect/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "refresh_token"),
				new("refresh_token", "rt-1"),
			}),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows.Count.ShouldBe(1);
		result.Flows[0].RelatedSessionIds.ShouldContain(4);
	}

	[Fact]
	public void Detect_CreatesOrphanRefreshTokenFlow_WhenNoOriginatingFlowFound() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "POST", "https://login.example.com/connect/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "refresh_token"),
				new("refresh_token", "orphan-refresh-token"),
			}),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows.Count.ShouldBe(1);
		result.Flows[0].FlowType.ShouldBe(AuthFlowType.RefreshToken);
		result.Flows[0].Warnings.ShouldContain(w => w.Kind == FlowWarningKind.IncompleteFlow);
	}

	[Fact]
	public void Detect_GroupsDeviceCodePolls_IntoOneFlow() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "POST", "https://login.example.com/devicecode", formBody: new List<FormBodyEntry> {
				new("client_id", "abc"),
				new("scope", "openid profile"),
			}, responseJson: JsonDocument.Parse("""
				{ "device_code": "dc-1", "user_code": "UC-1" }
			""").RootElement.Clone()),
			BuildSession(2, "POST", "https://login.example.com/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
				new("device_code", "dc-1"),
				new("client_id", "abc"),
			}, responseJson: JsonDocument.Parse("""{ "error": "authorization_pending" }""").RootElement.Clone(), statusCode: 400),
			BuildSession(3, "POST", "https://login.example.com/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
				new("device_code", "dc-1"),
				new("client_id", "abc"),
			}, responseJson: JsonDocument.Parse("""{ "access_token": "at-1" }""").RootElement.Clone()),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows.Count.ShouldBe(1);
		var flow = result.Flows[0];
		flow.FlowType.ShouldBe(AuthFlowType.DeviceCode);
		flow.RelatedSessionIds.ShouldBe([1, 2, 3]);
		flow.TokenRequestSessionId.ShouldBe(3);
	}

	[Fact]
	public void Detect_FallsBackToSequenceMatch_WhenStateAndRedirectUriDoNotMatch() {
		// Given: authorize has no redirect_uri and the callback's state does not match, forcing sequence-only correlation.
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://login.example.com/connect/authorize?client_id=abc&response_type=code&state=st-1"),
			BuildSession(2, "GET", "https://login.example.com/callback?code=auth-code-1&state=st-lost"),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows.Count.ShouldBe(1);
		var flow = result.Flows[0];
		flow.AuthorizationCallbackSessionId.ShouldBe(2);
		flow.Confidence.ShouldBeLessThan(1.0);
		flow.ConfidenceReasons.ShouldContain("sequence-only fallback");
	}

	[Fact]
	public void Detect_FlagsPkceMismatch_WhenCodeVerifierMissingFromTokenRequest() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://login.example.com/connect/authorize?client_id=abc&response_type=code&code_challenge=xyz&state=st-1"),
			BuildSession(2, "GET", "https://login.example.com/callback?code=auth-code-1&state=st-1"),
			BuildSession(3, "POST", "https://login.example.com/connect/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "auth-code-1"),
			}),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows[0].Warnings.ShouldContain(w => w.Kind == FlowWarningKind.PkceMismatch);
	}

	[Fact]
	public void Detect_FlagsMissingCallback_WhenAuthorizationRequestHasNoFollowUp() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://login.example.com/connect/authorize?client_id=abc&response_type=code&state=st-1"),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows[0].Warnings.ShouldContain(w => w.Kind == FlowWarningKind.MissingCallback);
	}

	[Fact]
	public void Detect_FlagsMissingTokenExchange_WhenCallbackHasNoTokenRequest() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://login.example.com/connect/authorize?client_id=abc&response_type=code&state=st-1"),
			BuildSession(2, "GET", "https://login.example.com/callback?code=auth-code-1&state=st-1"),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows[0].Warnings.ShouldContain(w => w.Kind == FlowWarningKind.MissingTokenExchange);
	}

	[Fact]
	public void Detect_FlagsUnsafeImplicitFlow_ForImplicitResponseType() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://login.example.com/connect/authorize?client_id=abc&response_type=token&state=st-1"),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		var flow = result.Flows[0];
		flow.FlowType.ShouldBe(AuthFlowType.Implicit);
		flow.Warnings.ShouldContain(w => w.Kind == FlowWarningKind.UnsafeImplicitFlow);
	}

	[Fact]
	public void Detect_FlagsMissingDiscoveryDocument_WhenNoneObserved() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://login.example.com/connect/authorize?client_id=abc&response_type=code&state=st-1"),
			BuildSession(2, "GET", "https://login.example.com/callback?code=auth-code-1&state=st-1"),
			BuildSession(3, "POST", "https://login.example.com/connect/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "auth-code-1"),
			}),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows[0].Warnings.ShouldContain(w => w.Kind == FlowWarningKind.MissingDiscoveryDocument);
	}

	[Fact]
	public void Detect_IncludesPkceReplayRequirements_ForPkceFlow() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://login.example.com/connect/authorize?client_id=abc&response_type=code&code_challenge=xyz&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcallback&state=st-1"),
			BuildSession(2, "GET", "https://app.example.com/callback?code=auth-code-1&state=st-1"),
			BuildSession(3, "POST", "https://login.example.com/connect/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "authorization_code"),
				new("code", "auth-code-1"),
				new("code_verifier", "verifier-1"),
			}),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		var kinds = result.Flows[0].ReplayRequirements.Select(r => r.Kind).ToHashSet();
		kinds.ShouldContain(ReplayRequirementKind.GenerateState);
		kinds.ShouldContain(ReplayRequirementKind.GenerateCodeVerifier);
		kinds.ShouldContain(ReplayRequirementKind.DeriveCodeChallenge);
		kinds.ShouldContain(ReplayRequirementKind.RequireCodeVerifier);
		kinds.ShouldContain(ReplayRequirementKind.DoNotReuseAuthorizationCode);
	}

	[Fact]
	public void Detect_IncludesUsernamePasswordCredentials_WhenPresentInAuthCallbackRequest() {
		// Given
		var sessions = new List<Session> {
			BuildSession(
				100,
				"POST",
				"https://app.example.com/login",
				formBody: new List<FormBodyEntry> {
					new("username", "user@example.com"),
					new("password", "SecurePass123"),
				},
				responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
					["Location"] = "https://app.example.com/callback?code=abc&state=xyz",
				},
				statusCode: 302
			) with {
				Request = BuildSession(100, "POST", "https://app.example.com/login", formBody: new List<FormBodyEntry> {
					new("username", "user@example.com"),
					new("password", "SecurePass123"),
				}).Request with {
					RequestType = RequestType.AuthorizationCallbackRequest,
				}
			}
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows.Count.ShouldBe(1);
		result.Flows[0].AuthenticationMethod.ShouldBeOfType<UsernamePasswordCredentials>();
		var credentials = (UsernamePasswordCredentials)result.Flows[0].AuthenticationMethod!;
		credentials.Username.ShouldBe("user@example.com");
		credentials.Password.ShouldBe("SecurePass123");
	}

	[Fact]
	public void Detect_IncludesSessionCookieCredentials_WhenPresentInAuthCallbackRequest() {
		// Given
		var sessionCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
			["x-ms-cpim-sso"] = "sso-cookie-value-123",
		};

		var sessions = new List<Session> {
			BuildSession(101, "GET", "https://app.example.com/protected", cookies: sessionCookies) with {
				Request = BuildSession(101, "GET", "https://app.example.com/protected", cookies: sessionCookies).Request with {
					RequestType = RequestType.AuthorizationCallbackRequest,
				}
			}
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows.Count.ShouldBe(1);
		result.Flows[0].AuthenticationMethod.ShouldBeOfType<SessionCookieCredentials>();
		var credentials = (SessionCookieCredentials)result.Flows[0].AuthenticationMethod!;
		credentials.CookieName.ShouldBe("x-ms-cpim-sso");
		credentials.CookieValue.ShouldBe("sso-cookie-value-123");
	}
}
