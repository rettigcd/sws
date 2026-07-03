namespace Auth;

/// <summary>Per-session classification result, kept flat for debugging/inspection alongside the correlated flows.</summary>
internal sealed record RequestClassification(
	int SessionId,
	RequestType RequestType,
	ResponseType ResponseType
);

internal sealed record AuthFlowDetectionResult(
	DateTimeOffset GeneratedUtc,
	List<DetectedAuthenticationFlow> Flows,
	List<OidcDiscoveryDocument> DiscoveryDocuments,
	List<RequestClassification> SessionClassifications,
	List<FlowWarning> GlobalWarnings
);
