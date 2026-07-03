namespace sws.Tests;

using Auth;
using Automation;
using Shouldly;
using Xunit;
using static sws.Tests.TestSessionBuilder;

public class EndpointResolver_Tests {

	static DetectedAuthenticationFlow BuildFlow(
		OidcDiscoveryDocument? discovery = null,
		AzureB2c.B2cFlowDetails? b2cDetails = null,
		int? authorizationRequestSessionId = null,
		int? tokenRequestSessionId = null
	) {
		return new DetectedAuthenticationFlow(
			FlowId: "flow-1",
			FlowType: AuthFlowType.AuthorizationCodeWithPkce,
			Confidence: 1.0,
			ConfidenceReasons: [],
			IsAzureB2c: b2cDetails is not null,
			B2cDetails: b2cDetails,
			Discovery: discovery,
			DiscoveryRequestSessionId: null,
			AuthorizationRequestSessionId: authorizationRequestSessionId,
			AuthorizationCallbackSessionId: null,
			TokenRequestSessionId: tokenRequestSessionId,
			RelatedSessionIds: [],
			Issuer: null,
			ClientId: "client-1",
			RedirectUri: "https://app.example.com/callback",
			Scopes: [],
			AuthenticationMethod: null,
			Variables: [],
			ReplayRequirements: [],
			Warnings: []
		);
	}

	[Fact]
	public void Resolve_UsesDiscoveryEndpoints_WhenPresent() {
		var discovery = new OidcDiscoveryDocument(1, "https://idp.example.com/", "https://idp.example.com/authorize", "https://idp.example.com/token", null, null, null, null, [], [], [], [], []);
		var flow = BuildFlow(discovery: discovery);

		var resolved = EndpointResolver.Resolve(flow, []);

		resolved.AuthorizationEndpoint.ShouldBe("https://idp.example.com/authorize");
		resolved.TokenEndpoint.ShouldBe("https://idp.example.com/token");
		resolved.Source.ShouldBe("discovery");
	}

	[Fact]
	public void Resolve_FallsBackToB2cDetails_WhenDiscoveryAbsent() {
		var b2cDetails = new AzureB2c.B2cFlowDetails(
			"tenant.onmicrosoft.com", "b2c_1a_signin", "https://tenant.b2clogin.com",
			"https://tenant.b2clogin.com/authorize", "https://tenant.b2clogin.com/token",
			"https://app.example.com/callback", "client-1", "fragment", "code", [], null, null, []
		);
		var flow = BuildFlow(b2cDetails: b2cDetails);

		var resolved = EndpointResolver.Resolve(flow, []);

		resolved.AuthorizationEndpoint.ShouldBe("https://tenant.b2clogin.com/authorize");
		resolved.TokenEndpoint.ShouldBe("https://tenant.b2clogin.com/token");
		resolved.Source.ShouldBe("b2c-details");
	}

	[Fact]
	public void Resolve_FallsBackToCapturedSessionUrl_WhenNeitherDiscoveryNorB2cDetailsPresent() {
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://login.example.com/connect/authorize?client_id=abc&response_type=code"),
			BuildSession(2, "POST", "https://login.example.com/connect/token"),
		};
		var flow = BuildFlow(authorizationRequestSessionId: 1, tokenRequestSessionId: 2);

		var resolved = EndpointResolver.Resolve(flow, sessions);

		resolved.AuthorizationEndpoint.ShouldBe("https://login.example.com/connect/authorize");
		resolved.TokenEndpoint.ShouldBe("https://login.example.com/connect/token");
		resolved.Source.ShouldBe("captured-session:1");
	}

	[Fact]
	public void Resolve_ReturnsUnresolved_WhenNothingAvailable() {
		var flow = BuildFlow();

		var resolved = EndpointResolver.Resolve(flow, []);

		resolved.AuthorizationEndpoint.ShouldBeNull();
		resolved.TokenEndpoint.ShouldBeNull();
		resolved.Source.ShouldBe("unresolved");
	}
}
