namespace Saz;

using System.Globalization;

/// <summary>
/// Reads a .saz capture into memory as a <see cref="Session"/> of <see cref="Exchange"/>s.
/// Faithful to the file: nothing is filtered, classified or removed.
/// </summary>
internal static class SazReader {

	public static Session Read(string sazPath) {
		var map = SazArchiveReader.LoadExchangeRawMap(sazPath);

		var exchanges = map
			.OrderBy(kvp => kvp.Key)
			.Select(kvp => BuildExchange(kvp.Value))
			.ToList();

		return new Session(Path.GetFullPath(sazPath), exchanges);
	}

	static Exchange BuildExchange(ExchangeRaw raw) {
		var metadata = SazMetadataParser.Parse(raw.MetadataBytes, raw.Id);

		DateTimeOffset? timestamp = null;
		if (metadata.Timers.TryGetValue("ClientBeginRequest", out var tsStr)
			&& DateTimeOffset.TryParse(tsStr, null, DateTimeStyles.RoundtripKind, out var ts))
			timestamp = ts;
		var request = SazHttpMessageParser.Parse(raw.ClientRequestBytes, isRequest: true);
		var response = SazHttpMessageParser.Parse(raw.ServerResponseBytes, isRequest: false);

		var requestLine = ParseRequestLine(request.StartLine);
		var statusLine = ParseStatusLine(response.StartLine);
		string? hostHeader = GetHeader(request.Headers, "Host");

		string url = BuildUrl(requestLine.Method, requestLine.Target, hostHeader, metadata);
		string requestBodyText = BodyAnalyzer.DecodeBodyText(request.Headers, request.BodyBytes);
		var requestBody = BodyAnalyzer.BuildBodyPlan(request.Headers, request.BodyBytes);
		var requestJsonBody = BodyAnalyzer.BuildRequestJsonBody(requestBody.Format, requestBodyText);
		var requestFormBody = BodyAnalyzer.BuildRequestFormBody(requestBody.Format, requestBodyText);
		string responseBodyText = BodyAnalyzer.DecodeBodyText(response.Headers, response.BodyBytes);
		var responseBody = BodyAnalyzer.BuildBodyPlan(response.Headers, response.BodyBytes);
		string? responseText = BodyAnalyzer.BuildResponseText(response.Headers, responseBodyText);
		var responseJson = BodyAnalyzer.BuildResponseJson(responseBody.Format, responseBodyText);

		var requestModel = new Request(
			request.StartLine,
			requestLine.Method,
			requestLine.Target,
			requestLine.Version,
			url,
			hostHeader,
			ParseQueryParameters(url),
			ExtractFragment(url, requestLine.Target),
			SortHeaders(request.Headers),
			ParseCookies(GetHeader(request.Headers, "Cookie")),
			requestBody,
			requestJsonBody,
			requestFormBody
		);

		var responseModel = new Response(
			response.StartLine,
			statusLine.Code,
			statusLine.ReasonPhrase,
			SortHeaders(response.Headers),
			responseBody,
			responseText,
			responseJson
		);

		return new Exchange(
			raw.Id,
			timestamp,
			metadata,
			requestModel,
			responseModel
		);
	}

	static string? ExtractFragment(string url, string target) {
		if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUrl)) {
			string fragment = absoluteUrl.Fragment.TrimStart('#');
			if (!string.IsNullOrWhiteSpace(fragment))
				return fragment;
		}

		if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteTarget)) {
			string fragment = absoluteTarget.Fragment.TrimStart('#');
			if (!string.IsNullOrWhiteSpace(fragment))
				return fragment;
		}

		int hashIndex = target.IndexOf('#', StringComparison.Ordinal);
		if (hashIndex >= 0 && hashIndex + 1 < target.Length)
			return target[(hashIndex + 1)..];

		return null;
	}
	static RequestLineParts ParseRequestLine(string startLine) {
		var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
		string method = parts.Length > 0 ? parts[0] : "GET";
		string target = parts.Length > 1 ? parts[1] : "/";
		string version = parts.Length > 2 ? parts[2] : "HTTP/1.1";
		return new RequestLineParts(method, target, version);
	}

	static StatusLineParts ParseStatusLine(string startLine) {
		var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

		int code = 0;
		if (parts.Length > 1)
			int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out code);

		string reason = parts.Length > 2 ? parts[2] : string.Empty;
		return new StatusLineParts(code, reason);
	}

	static string BuildUrl(string method, string target, string? host, Metadata? metadata) {
		if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
			return absolute.ToString();

		if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
			return "https://" + target;

		string scheme = metadata?.Flags.TryGetValue("x-overrideGateway", out string? gateway) == true
			&& gateway.Contains("https", StringComparison.OrdinalIgnoreCase)
			? "https"
			: "http";

		if (!string.IsNullOrWhiteSpace(host)) {
			string normalizedTarget = target.StartsWith('/') ? target : "/" + target;
			return $"{scheme}://{host}{normalizedTarget}";
		}

		return target;
	}

	static Dictionary<string, string> ParseQueryParameters(string url) {
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Query))
			return result;


		string query = uri.Query.TrimStart('?');
		foreach (string item in query.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = item.Split('=', 2);
			string name = Uri.UnescapeDataString(parts[0]);
			string value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
			result[name] = value;
		}

		return result;
	}

	static Dictionary<string, string> ParseCookies(string? cookieHeader) {
		var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		if (string.IsNullOrWhiteSpace(cookieHeader))
			return cookies;

		foreach (string piece in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = piece.Split('=', 2);
			if (parts.Length == 0)
				continue;

			string key = parts[0].Trim();
			string value = parts.Length > 1 ? parts[1].Trim() : string.Empty;

			if (!string.IsNullOrWhiteSpace(key))
				cookies[key] = value;

		}

		return cookies;
	}

	static string? GetHeader(Dictionary<string, string> headers, string name) {
		return headers.TryGetValue(name, out string? value) ? value : null;
	}

	private static Dictionary<string, string> SortHeaders(Dictionary<string, string> headers) {
		return headers
			.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
	}
}
