namespace Analysis;

using Saz;
using System.Text.Json;

internal static class SourceReportBuilder {
	public static AzureB2cSourceContext BuildAzureB2cSourceContext(IReadOnlyList<Exchange> exchanges) {
		var flowExchangeIds = Auth.AuthFlowDetector
			.Detect(exchanges)
			.Flows
			.Where(flow => flow.IsAzureB2c)
			.SelectMany(flow => flow.RelatedExchangeIds)
			.ToHashSet();

		return new AzureB2cSourceContext(flowExchangeIds);
	}

	public static RequestSources BuildExchangeSourcesReport(
		int exchangeIndex,
		IReadOnlyList<Exchange> exchanges,
		Dictionary<string, string>? missing = null,
		Dictionary<string, string>? unsourcedCookies = null,
		AzureB2cSourceContext? azureB2cSourceContext = null
	) {
		var targetExchange = exchanges[exchangeIndex];
		var previousExchanges = exchanges.Take(exchangeIndex).ToList();
		azureB2cSourceContext ??= BuildAzureB2cSourceContext(exchanges);
		bool isAzureB2cFlowExchange = azureB2cSourceContext.FlowExchangeIds.Contains(targetExchange.ExchangeId);
		var unsourcedCookieList = CookieSourceService.BuildUnsourcedRequestCookies(
			targetExchange,
			previousExchanges,
			isAzureB2cFlowExchange,
			GetOrderedSources
		);
		CookieSourceService.RegisterUnsourcedCookies(unsourcedCookies, unsourcedCookieList, RegisterDictionaryValue);
		var requestForPlan = CookieSourceService.BuildRequestForCookieJar(targetExchange.Request, unsourcedCookieList);
		var requestPlan = new RequestPlan(requestForPlan);
		ReplacementSourceResolver.PopulateReplacementSources(
			requestPlan,
			previousExchanges,
			missing,
			isAzureB2cFlowExchange,
			GetOrderedSources,
			RegisterMissingValue
		);
		var requestType = Auth.ExchangeClassifier.ClassifyRequest(targetExchange, previousExchanges);

		return new RequestSources(
			exchangeIndex,
			targetExchange.ExchangeId,
			targetExchange.Request.Method,
			targetExchange.Request.Url,
			requestType,
			requestPlan,
			null
		);
	}

	static List<RequestSourceFinding> BuildRequestSourceFindings(
		Exchange targetExchange,
		RequestPlan requestPlan,
		IReadOnlyList<Exchange> exchanges,
		int exchangeIndex,
		Dictionary<string, string>? missing = null
	) {
		var previousExchanges = exchanges.Take(exchangeIndex).ToList();
		var findings = new List<RequestSourceFinding>();

		foreach (var piece in BuildRequestPieces(targetExchange)) {
			if (piece.PieceKind == RequestSourcePieceKind.Path) {
				findings.AddRange(BuildPathSourceFindings(piece, previousExchanges, missing));
				continue;
			}

			var orderedSources = GetOrderedSources(previousExchanges, piece.Value);
			findings.Add(BuildFinding(piece, orderedSources.FirstOrDefault(), missing));
		}

		bool isAzureB2cFlowExchange = BuildAzureB2cSourceContext(exchanges).FlowExchangeIds.Contains(targetExchange.ExchangeId);
		ReplacementSourceResolver.PopulateReplacementSources(
			requestPlan,
			previousExchanges,
			missing,
			isAzureB2cFlowExchange,
			GetOrderedSources,
			RegisterMissingValue
		);

		return findings;
	}

	static List<RequestSourceFinding> BuildPathSourceFindings(
		RequestPiece pathPiece,
		IReadOnlyList<Exchange> previousExchanges,
		Dictionary<string, string>? missing
	) {
		var findings = new List<RequestSourceFinding>();
		string remaining = NormalizePathPart(pathPiece.Value);

		while (!string.IsNullOrWhiteSpace(remaining)) {
			string? foundPath = FindPathMatchWithProgressiveTrim(remaining, previousExchanges, out var source);
			if (string.IsNullOrWhiteSpace(foundPath) || source is null) {
				findings.Add(BuildFinding(new RequestPiece(pathPiece.PieceKind, pathPiece.Name, remaining), null, missing));
				break;
			}

			findings.Add(new RequestSourceFinding(pathPiece with {Value = foundPath }, source));

			if (string.Equals(foundPath, remaining, StringComparison.Ordinal))
				break;

			string unfound = remaining[foundPath.Length..];
			remaining = NormalizePathPart(unfound);
		}

		return findings;
	}

	static string? FindPathMatchWithProgressiveTrim(
		string path,
		IReadOnlyList<Exchange> previousExchanges,
		out SourceFinding? source
	) {
		source = null;
		string candidate = NormalizePathPart(path);

		while (!string.IsNullOrWhiteSpace(candidate)) {
			var orderedSources = GetOrderedSources(previousExchanges, candidate);
			source = orderedSources.FirstOrDefault();
			if (source is not null)
				return candidate;

			candidate = TrimDeepestSegment(candidate);
		}

		return null;
	}

	static List<SourceFinding> GetOrderedSources(IReadOnlyList<Exchange> previousExchanges, string needle) {
		var sources = new HashSet<SourceFinding>();
		foreach (var previousExchange in previousExchanges)
			foreach (var source in FindSourcesInPreviousResponse(previousExchange, needle))
				sources.Add(source);

		return sources
			.OrderBy(source => source.ExchangeId)
			.ThenBy(source => source.SourceKind, StringComparer.OrdinalIgnoreCase)
			.ThenBy(source => source.SourceName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(source => source.Needle, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	static RequestSourceFinding BuildFinding(
		RequestPiece piece,
		SourceFinding? source,
		Dictionary<string, string>? missing
	) {
		if (source is not null)
			return new RequestSourceFinding(piece, source);

		string preferredKey = BuildPlaceholderName(piece);
		if (missing is not null) {
			string missingKey = RegisterMissingValue(missing, preferredKey, piece.Value);
			return new RequestSourceFinding(piece, new MissingSourceReference(missingKey));
		}

		return new RequestSourceFinding(piece, new MissingSourceReference(preferredKey));
	}

	static string BuildPlaceholderName(RequestPiece piece) {
		string name = string.IsNullOrWhiteSpace(piece.Name) ? "value" : piece.Name.Trim();
		return $"{{{name}}}";
	}

	static string RegisterMissingValue(Dictionary<string, string> missing, string preferredKey, string value) {
		return RegisterDictionaryValue(missing, preferredKey, value);
	}

	static string RegisterDictionaryValue(Dictionary<string, string> dictionary, string preferredKey, string value) {
		string baseKey = string.IsNullOrWhiteSpace(preferredKey) ? "{value}" : preferredKey;

		if (!dictionary.TryGetValue(baseKey, out string? existingValue)) {
			dictionary[baseKey] = value;
			return baseKey;
		}

		if (string.Equals(existingValue, value, StringComparison.Ordinal))
			return baseKey;

		int suffix = 1;
		while (true) {
			string collisionKey = AppendCollisionSuffix(baseKey, suffix);
			if (!dictionary.TryGetValue(collisionKey, out existingValue)) {
				dictionary[collisionKey] = value;
				return collisionKey;
			}

			if (string.Equals(existingValue, value, StringComparison.Ordinal))
				return collisionKey;

			suffix++;
		}
	}

	static string AppendCollisionSuffix(string key, int suffix) {
		if (key.EndsWith("}", StringComparison.Ordinal) && key.Length > 1)
			return $"{key[..^1]}_{suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

		return $"{key}_{suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
	}

	static string NormalizePathPart(string path) {
		if (string.IsNullOrWhiteSpace(path))
			return string.Empty;

		string trimmed = path.Trim();
		if (!trimmed.StartsWith("/", StringComparison.Ordinal))
			trimmed = "/" + trimmed;

		while (trimmed.Length > 1 && trimmed.EndsWith("/", StringComparison.Ordinal))
			trimmed = trimmed[..^1];


		return trimmed;
	}

	static string TrimDeepestSegment(string path) {
		if (string.IsNullOrWhiteSpace(path))
			return string.Empty;


		string normalized = NormalizePathPart(path);
		int lastSlash = normalized.LastIndexOf('/');
		if (lastSlash <= 0)
			return string.Empty;

		return normalized[..lastSlash];
	}

	static List<RequestPiece> BuildRequestPieces(Exchange targetExchange) {
		var pieces = new List<RequestPiece>();

		if (!string.IsNullOrWhiteSpace(targetExchange.Request.Host))
			pieces.Add(new RequestPiece(RequestSourcePieceKind.Host, "host", targetExchange.Request.Host));

		string path = GetRequestPath(targetExchange.Request);
		if (!string.IsNullOrWhiteSpace(path) && !string.Equals(path, "/", StringComparison.Ordinal))
			pieces.Add(new RequestPiece(RequestSourcePieceKind.Path, "path", path));

		foreach (var queryParameter in targetExchange.Request.QueryParameters.OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase))
			if (!string.IsNullOrWhiteSpace(queryParameter.Value))
				pieces.Add(new RequestPiece(RequestSourcePieceKind.QueryParameter, queryParameter.Key, queryParameter.Value));

		if (string.Equals(targetExchange.Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
			var bodyParameters = ExtractBodyParameters(targetExchange.Request);
			foreach (var param in bodyParameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
				if (!string.IsNullOrWhiteSpace(param.Value))
					pieces.Add(new RequestPiece(RequestSourcePieceKind.BodyParameter, param.Key, param.Value));
		}

		return pieces;
	}

	static string GetRequestPath(Request request) {
		if (Uri.TryCreate(request.Url, UriKind.Absolute, out var absoluteUri))
			return absoluteUri.AbsolutePath;

		string target = request.Target.Split('?', 2)[0];
		return target;
	}

	static List<FormBodyEntry> ExtractBodyParameters(Request request) {
		var parameters = new List<FormBodyEntry>();

		if (request.FormBody is { Count: > 0 }) {
			foreach (var pair in request.FormBody)
				if (!string.IsNullOrWhiteSpace(pair.Key))
					parameters.Add(pair);
			return parameters;
		}

		if (request.Body.Length == 0 || string.IsNullOrWhiteSpace(request.Body.Format))
			return parameters;

		// Fallback for non-form bodies where parsed form pairs are unavailable.
		foreach (string hint in request.Body.SchemaHints)
			if (!string.IsNullOrWhiteSpace(hint))
				parameters.Add(new FormBodyEntry(hint, hint));

		return parameters;
	}

	static IEnumerable<SourceFinding> FindSourcesInPreviousResponse(Exchange previousExchange, string needle) {
		if (string.IsNullOrWhiteSpace(needle))
			yield break;

		if (!string.IsNullOrWhiteSpace(previousExchange.Request.Host) && ContainsInsensitive(previousExchange.Request.Host, needle))
			yield return new SourceFinding(previousExchange.ExchangeId, "request host", "host", needle);

		foreach (var header in previousExchange.Response.Headers.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase))
			if (ContainsInsensitive(header.Value, needle))
				yield return new SourceFinding(previousExchange.ExchangeId, "response header", header.Key, needle);

		if (!string.IsNullOrWhiteSpace(previousExchange.Response.ResponseText) && ContainsInsensitive(previousExchange.Response.ResponseText, needle))
			yield return new SourceFinding(previousExchange.ExchangeId, "response body text", null, needle);

		if (previousExchange.Response.ResponseJson is JsonElement responseJson)
			foreach (var match in FindJsonValueMatches(responseJson, needle, "$"))
				yield return new SourceFinding(previousExchange.ExchangeId, "response JSON", match.Path, needle);

	}

	static IEnumerable<JsonValueMatch> FindJsonValueMatches(JsonElement element, string needle, string path) {
		switch (element.ValueKind) {
			case JsonValueKind.Object:
				foreach (var property in element.EnumerateObject()) {
					string propertyPath = path == "$" ? $"$.{property.Name}" : $"{path}.{property.Name}";
					foreach (var match in FindJsonValueMatches(property.Value, needle, propertyPath)) {
						yield return match;
					}
				}
				break;
			case JsonValueKind.Array:
				int index = 0;
				foreach (var item in element.EnumerateArray()) {
					string itemPath = $"{path}[{index++}]";
					foreach (var match in FindJsonValueMatches(item, needle, itemPath)) {
						yield return match;
					}
				}
				break;
			case JsonValueKind.String:
				string stringValue = element.GetString() ?? string.Empty;
				if (ContainsInsensitive(stringValue, needle)) {
					yield return new JsonValueMatch(path, stringValue);
				}
				break;
			case JsonValueKind.Number:
			case JsonValueKind.True:
			case JsonValueKind.False:
				string scalarValue = element.ToString();
				if (ContainsInsensitive(scalarValue, needle)) {
					yield return new JsonValueMatch(path, scalarValue);
				}
				break;
		}
	}

	static bool ContainsInsensitive(string haystack, string needle) {
		return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
	}

	sealed record JsonValueMatch(
		string Path,
		string Value
	);
}

// String Fragment used in a request
sealed record RequestPiece(
	// where it is used
	RequestSourcePieceKind PieceKind,
	// the name of for query/body parameters
	string Name,
	// the value we are looking for
	string Value
);

internal sealed record AzureB2cSourceContext(
	HashSet<int> FlowExchangeIds
);
