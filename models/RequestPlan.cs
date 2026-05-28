using System.Text.Json;

internal sealed record RequestPlan(
	string OriginalStartLine,
	string Method,
	string Target,
	string Version,
	string Url,
	string? Host,
	Dictionary<string, string> QueryParameters,
	Dictionary<string, string> Headers,
	Dictionary<string, string> Cookies,
	List<string> DynamicHeaders,
	BodyPlan Body,
	JsonElement? JsonBody,
	List<FormBodyEntry>? FormBody,
	List<string> RegenerationSteps
);
