using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record Response(
	string OriginalStartLine,
	int StatusCode,
	string ReasonPhrase,
	Dictionary<string, string> Headers,
	Body Body,
	string? ResponseText,
	JsonElement? ResponseJson,
	List<string> VerificationSteps,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Auth.ResponseType ResponseClassification = Auth.ResponseType.Unknown
);
