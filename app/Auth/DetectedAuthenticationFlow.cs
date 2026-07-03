using System.Text.Json.Serialization;

namespace Auth;

/// <summary>
/// A group of related sessions correlated into a single authentication flow.
///
/// This is the contract a future replay engine (not built here) is expected to consume:
/// endpoints (Discovery, or heuristically classified via the related sessions), ClientId/
/// RedirectUri/Scopes (Configuration variables), PKCE derivation info (Variables with
/// Category=Derived), and ReplayRequirements describing what must be regenerated vs. preserved.
/// </summary>
internal sealed record DetectedAuthenticationFlow(
	string FlowId,
	AuthFlowType FlowType,
	double Confidence,
	List<string> ConfidenceReasons,

	bool IsAzureB2c,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AzureB2c.B2cFlowDetails? B2cDetails,

	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OidcDiscoveryDocument? Discovery,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DiscoveryRequestSessionId,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AuthorizationRequestSessionId,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AuthorizationCallbackSessionId,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TokenRequestSessionId,

	List<int> RelatedSessionIds,

	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Issuer,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientId,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RedirectUri,
	List<string> Scopes,

	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthenticationCredentials? AuthenticationMethod,

	List<Variable> Variables,
	List<ReplayRequirement> ReplayRequirements,
	List<FlowWarning> Warnings
);
