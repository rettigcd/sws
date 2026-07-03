using System.Text.Json.Serialization;

namespace Auth;

/// <summary>
/// A single named value observed while participating in a detected authentication flow.
/// Per spec, values are never redacted.
/// </summary>
internal sealed record Variable(
	string Name,
	string Value,
	VariableCategory Category,
	VariableSource Source,
	int SessionId,

	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? JsonPath = null,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DerivedFromVariableName = null,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notes = null
);
