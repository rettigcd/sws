namespace sws.Tests;

using System.Net.Http;
using System.Text.Json;
using Auth;
using Automation;
using Shouldly;
using Xunit;
using static sws.Tests.TestSessionBuilder;

public class AuthFlowAutomationEngine_RefreshToken_Tests {

	[Fact]
	public async Task ExecuteAsync_RefreshesAccessToken_UsingSingleTokenEndpointCall() {
		var sessions = new List<Session> {
			BuildSession(1, "POST", "https://login.example.com/connect/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "refresh_token"),
				new("refresh_token", "captured-refresh-token"),
				new("client_id", "client-1"),
			}),
		};
		var detected = AuthFlowDetector.Detect(sessions);
		var flow = detected.Flows.Single();
		flow.FlowType.ShouldBe(AuthFlowType.RefreshToken);

		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Post, "https://login.example.com/connect/token", request => FakeResponses.Json("""
			{ "access_token": "fresh-access-token", "token_type": "Bearer", "expires_in": 3600, "refresh_token": "rotated-refresh-token" }
		"""));

		var result = await AuthFlowAutomationEngine.ExecuteAsync(flow, sessions, new AutomationOptions(HttpClient: fakeHttpClient));

		result.Success.ShouldBeTrue();
		result.Tokens.ShouldNotBeNull();
		result.Tokens!.AccessToken.ShouldBe("fresh-access-token");
		result.Tokens.RefreshToken.ShouldBe("rotated-refresh-token");
		fakeHttpClient.SentRequests.Count.ShouldBe(1);
	}

	[Fact]
	public async Task ExecuteAsync_PrefersRefreshTokenOverride_OverCapturedValue() {
		var sessions = new List<Session> {
			BuildSession(1, "POST", "https://login.example.com/connect/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "refresh_token"),
				new("refresh_token", "captured-refresh-token-should-not-be-used"),
				new("client_id", "client-1"),
			}),
		};
		var flow = AuthFlowDetector.Detect(sessions).Flows.Single();

		var fakeHttpClient = new FakeAuthHttpClient();
		string? capturedRequestBody = null;
		fakeHttpClient.Enqueue(req => req.Method == HttpMethod.Post, request => {
			capturedRequestBody = request.Content!.ReadAsStringAsync().Result;
			return FakeResponses.Json("""{ "access_token": "at" }""");
		});

		var result = await AuthFlowAutomationEngine.ExecuteAsync(
			flow, sessions,
			new AutomationOptions(HttpClient: fakeHttpClient, RefreshTokenOverride: "override-token")
		);

		result.Success.ShouldBeTrue();
		capturedRequestBody.ShouldNotBeNull();
		capturedRequestBody.ShouldContain("override-token");
		capturedRequestBody.ShouldNotContain("captured-refresh-token-should-not-be-used");
	}

	[Fact]
	public async Task RefreshAccessTokenAsync_Standalone_WorksWithoutOriginalFlowOrSessions() {
		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Post, "https://login.example.com/connect/token", request => FakeResponses.Json("""
			{ "access_token": "standalone-access-token", "expires_in": 60 }
		"""));

		var result = await AuthFlowAutomationEngine.RefreshAccessTokenAsync(
			"https://login.example.com/connect/token",
			"client-1",
			null,
			"a-refresh-token",
			new AutomationOptions(HttpClient: fakeHttpClient)
		);

		result.Success.ShouldBeTrue();
		result.Tokens!.AccessToken.ShouldBe("standalone-access-token");
	}

	[Fact]
	public async Task ExecuteAsync_ReturnsFailure_WhenNoRefreshTokenAvailable() {
		var sessions = new List<Session> {
			BuildSession(1, "POST", "https://login.example.com/connect/token", formBody: new List<FormBodyEntry> {
				new("grant_type", "client_credentials"),
			}),
		};
		var flow = new DetectedAuthenticationFlow(
			"flow-1", AuthFlowType.RefreshToken, 1.0, [], false, null, null, null, null, null, 1,
			[1], null, "client-1", null, [], null, [], [], []
		);

		var fakeHttpClient = new FakeAuthHttpClient();

		var result = await AuthFlowAutomationEngine.ExecuteAsync(flow, sessions, new AutomationOptions(HttpClient: fakeHttpClient));

		result.Success.ShouldBeFalse();
		result.UnsupportedReason!.Kind.ShouldBe(UnsupportedFlowReasonKind.MissingCredentials);
		fakeHttpClient.SentRequests.ShouldBeEmpty();
	}
}
