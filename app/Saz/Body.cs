using System.Text.Json.Serialization;

internal sealed record Body(
	int Length,
	string? ContentType,
	string Format,
	[property: JsonIgnore]
	List<string> SchemaHints
);
