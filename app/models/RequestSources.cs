using System.Text.Json.Serialization;

internal sealed record RequestSources(
	int SourceSessionIndex,
	int SourceSessionId,
	string Method,
	string Url,
	RequestType RequestType,
	RequestPlan RequestPlan,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<RequestSourceFinding>? Findings
);

internal sealed record SessionSourcesBatchReport(
	string SourceBasePath,
	Dictionary<string, string> Missing,
	Dictionary<string, string> UnsourcedCookies,
	List<RequestSources> Mappings
);

internal sealed record RequestSourceFinding(

	RequestPiece Used,

	// where it is sourced from.
	object Source
);

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum RequestSourcePieceKind {
	Host,
	Path,
	QueryParameter,
	BodyParameter,
}

internal sealed record MissingSourceReference(
	string MissingKey
);

internal sealed record UnsourcedRequestCookie(
	string Name,
	string Value
);