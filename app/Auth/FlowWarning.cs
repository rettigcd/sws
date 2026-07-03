using System.Text.Json.Serialization;

namespace Auth;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum FlowWarningKind {
	MissingDiscoveryDocument,
	IncompleteFlow,
	MissingCallback,
	MissingTokenExchange,
	PkceMismatch,
	MissingClientSecret,
	MfaOrCaptchaSuspected,
	UnsafeImplicitFlow,
	SensitiveTokenExposure,
	Other,
}

internal sealed record FlowWarning(
	FlowWarningKind Kind,
	string Message,
	IReadOnlyList<int> RelatedSessionIds
);
