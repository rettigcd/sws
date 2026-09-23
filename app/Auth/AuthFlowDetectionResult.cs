namespace Auth;

/// <summary>Per-exchange classification result, kept flat for debugging/inspection alongside the correlated flows.</summary>
internal sealed record RequestClassification(
	int ExchangeId,
	RequestType RequestType,
	ResponseType ResponseType
);

internal sealed record AuthFlowDetectionResult(
	DateTimeOffset GeneratedUtc,
	List<DetectedAuthenticationFlow> Flows,
	List<OidcDiscoveryDocument> DiscoveryDocuments,
	List<RequestClassification> ExchangeClassifications,
	List<FlowWarning> GlobalWarnings
);
