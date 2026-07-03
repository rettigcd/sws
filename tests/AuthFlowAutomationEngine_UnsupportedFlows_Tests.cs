namespace sws.Tests;

using Auth;
using Automation;
using Shouldly;
using Xunit;

public class AuthFlowAutomationEngine_UnsupportedFlows_Tests {

	static DetectedAuthenticationFlow BuildFlow(AuthFlowType flowType) {
		return new DetectedAuthenticationFlow(
			FlowId: "flow-1",
			FlowType: flowType,
			Confidence: 1.0,
			ConfidenceReasons: [],
			IsAzureB2c: false,
			B2cDetails: null,
			Discovery: null,
			DiscoveryRequestSessionId: null,
			AuthorizationRequestSessionId: null,
			AuthorizationCallbackSessionId: null,
			TokenRequestSessionId: null,
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

	[Theory]
	[InlineData(nameof(AuthFlowType.ClientCredentials))]
	[InlineData(nameof(AuthFlowType.DeviceCode))]
	[InlineData(nameof(AuthFlowType.Implicit))]
	[InlineData(nameof(AuthFlowType.Hybrid))]
	[InlineData(nameof(AuthFlowType.ResourceOwnerPasswordCredentials))]
	[InlineData(nameof(AuthFlowType.Unknown))]
	public async Task ExecuteAsync_ReturnsUnsupportedFlowReason_AndMakesNoHttpCalls_ForOutOfScopeFlowTypes(string flowTypeName) {
		var fakeHttpClient = new FakeAuthHttpClient();
		var flow = BuildFlow(Enum.Parse<AuthFlowType>(flowTypeName));

		var result = await AuthFlowAutomationEngine.ExecuteAsync(flow, [], new AutomationOptions(HttpClient: fakeHttpClient));

		result.Success.ShouldBeFalse();
		result.UnsupportedReason.ShouldNotBeNull();
		result.UnsupportedReason!.Kind.ShouldBe(UnsupportedFlowReasonKind.UnsupportedFlowType);
		fakeHttpClient.SentRequests.ShouldBeEmpty();
	}
}
