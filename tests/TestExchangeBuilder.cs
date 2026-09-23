namespace sws.Tests;

using Saz;
using System.Text.Json;

static class TestExchangeBuilder {

	public static Exchange BuildExchange(
		int exchangeId,
		string method,
		string url,
		Dictionary<string, string>? query = null,
		List<FormBodyEntry>? formBody = null,
		JsonElement? responseJson = null,
		Dictionary<string, string>? responseHeaders = null,
		int statusCode = 200,
		Dictionary<string, string>? cookies = null,
		Dictionary<string, string>? requestHeaders = null,
		string? fragment = null,
		JsonElement? requestJson = null
	) {
		var uri = new Uri(url);
		var queryParameters = query ?? ParseQueryParameters(uri);

		var request = new Request(
			$"{method} {url} HTTP/1.1",
			method,
			url,
			"HTTP/1.1",
			url,
			uri.Host,
			queryParameters,
			fragment,
			requestHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			cookies ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			new Body(0, null, "none", new List<string>()),
			requestJson,
			formBody
		);

		var response = new Response(
			$"HTTP/1.1 {statusCode} OK",
			statusCode,
			"OK",
			responseHeaders ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			new Body(0, null, "none", new List<string>()),
			null,
			responseJson
		);

		return new Exchange(exchangeId, null, null, request, response);
	}

	static Dictionary<string, string> ParseQueryParameters(Uri uri) {
		var query = uri.Query;
		var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(query) || query == "?")
			return parameters;

		foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			var split = pair.Split('=', 2);
			var key = Uri.UnescapeDataString(split[0]);
			var value = split.Length > 1 ? Uri.UnescapeDataString(split[1]) : string.Empty;
			if (!string.IsNullOrWhiteSpace(key))
				parameters[key] = value;
		}

		return parameters;
	}
}
