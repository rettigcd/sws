using System.Text.Json;

internal sealed record ResponsePlan(
	string OriginalStartLine,
	int StatusCode,
	string ReasonPhrase,
	Dictionary<string, string> Headers,
	BodyPlan Body,
	string? ResponseText,
	JsonElement? ResponseJson,
	List<string> VerificationSteps
);
