internal sealed record SessionSourcesReport(
	int SourceSessionIndex,
	int SourceSessionId,
	string Method,
	string Url,
	List<RequestSourceFinding> Findings
);

internal sealed record SessionSourcesBatchReport(
	string SourceBasePath,
	List<string> Missing,
	List<SessionSourcesReport> Mappings
);

internal sealed record RequestSourceFinding(
	string PieceKind,
	string Name,
	string Value,
	object Source
);

internal sealed record MissingSourceReference(
	int MissingIndex
);