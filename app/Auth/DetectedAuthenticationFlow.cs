using System.Text.Json.Serialization;

namespace Auth;

/// <summary>
/// A group of related exchanges correlated into a single authentication flow.
///
/// This is the contract a future replay engine (not built here) is expected to consume:
/// endpoints (Discovery, or heuristically classified via the related exchanges), ClientId/
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
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DiscoveryRequestExchangeId,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AuthorizationRequestExchangeId,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AuthorizationCallbackExchangeId,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TokenRequestExchangeId,

	List<int> RelatedExchangeIds,

	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Issuer,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientId,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RedirectUri,
	List<string> Scopes,

	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthenticationCredentials? AuthenticationMethod,

	List<Variable> Variables,
	List<ReplayRequirement> ReplayRequirements,
	List<FlowWarning> Warnings
);
