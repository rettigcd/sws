namespace SazJson;

internal sealed record GlobalHeadersGroup(
	string Name,
	Dictionary<string, string> Headers
);
