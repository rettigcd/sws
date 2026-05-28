using System.Text.Json.Serialization;

internal sealed record BodyPlan(
	int Length,
	string? ContentType,
	string Format,
	[property: JsonIgnore]
	List<string> SchemaHints
);
