internal sealed record SazPlan(
	string SourceSazFile,
	DateTimeOffset GeneratedUtc,
	GlobalHeadersGroupPlan GlobalHeaders,
	List<SessionPlan> Sessions
);
