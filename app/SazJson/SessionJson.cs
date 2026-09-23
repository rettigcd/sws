internal sealed record Saz(
	string SourceFile,
	DateTimeOffset GeneratedUtc,
	GlobalHeadersGroup GlobalHeaders,
	List<Session> Sessions
);
