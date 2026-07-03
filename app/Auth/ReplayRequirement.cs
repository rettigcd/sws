using System.Text.Json.Serialization;

namespace Auth;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ReplayRequirementKind {
	UseDiscoveredEndpoint,
	GenerateState,
	GenerateNonce,
	GenerateCodeVerifier,
	DeriveCodeChallenge,
	PreserveClientId,
	PreserveRedirectUri,
	PreserveScopes,
	RequireClientSecret,
	RequireCodeVerifier,
	DoNotReuseAuthorizationCode,
	DoNotReuseTransientCookies,
	RequireInteractiveLogin,
	PossibleMfaOrCaptcha,
	Other,
}

/// <summary>
/// Something a future replay engine needs to do or know to reproduce this flow.
/// This detector does not perform replay itself; it only records requirements.
/// </summary>
internal sealed record ReplayRequirement(
	ReplayRequirementKind Kind,
	string Description,

	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RelatedVariableName = null
);
