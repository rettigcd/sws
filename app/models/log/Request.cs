using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record Request(
	string OriginalStartLine,
	string Method,
	string Target,
	string Version,
	string Url,
	string? Host,
	Dictionary<string, string> QueryParameters,
	string? Fragment,
	Dictionary<string, string> Headers,
	Dictionary<string, string> Cookies,
	List<string> DynamicHeaders,
	Body Body,
	JsonElement? JsonBody,
	List<FormBodyEntry>? FormBody,
	List<string> RegenerationSteps,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] RequestType RequestType = RequestType.Unknown
);
