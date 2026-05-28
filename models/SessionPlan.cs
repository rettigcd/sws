internal sealed record SessionPlan(
	int SessionId,
	MetadataPlan? Metadata,
	RequestPlan Request,
	ResponsePlan Response
);
