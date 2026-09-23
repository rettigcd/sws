namespace Saz;

using System.Text.Json;

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
	Body Body,
	JsonElement? JsonBody,
	List<FormBodyEntry>? FormBody
);
