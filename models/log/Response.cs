using System.Text.Json;

internal sealed record Response(
	string OriginalStartLine,
	int StatusCode,
	string ReasonPhrase,
	Dictionary<string, string> Headers,
	Body Body,
	string? ResponseText,
	JsonElement? ResponseJson,
	List<string> VerificationSteps
);
