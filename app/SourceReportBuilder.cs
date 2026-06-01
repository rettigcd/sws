using System.Text.Json;

internal static class SourceReportBuilder {
	static readonly HashSet<string> AzureB2cFlowValueKeys =
	[
		"client_id",
		"redirect_uri",
		"code_challenge",
		"code_verifier",
		"nonce",
		"state",
		"grant_type",
		"response_type",
		"response_mode",
		"scope",
		"prompt",
		"login_hint",
		"client-request-id",
	];

	static readonly string[] AzureB2cCookiePrefixes =
	[
		"x-ms-cpim-",
	];

	public static AzureB2cSourceContext BuildAzureB2cSourceContext(IReadOnlyList<Session> sessions) {
		var flowSessionIds = AzureB2cAuthenticationScanner
			.Scan(sessions)
			.Flows
			.SelectMany(flow => flow.SessionIds)
			.ToHashSet();

		return new AzureB2cSourceContext(flowSessionIds);
	}

	public static RequestSources BuildSessionSourcesReport(
		int sessionIndex,
		IReadOnlyList<Session> sessions,
		Dictionary<string, string>? missing = null,
		Dictionary<string, string>? unsourcedCookies = null,
		AzureB2cSourceContext? azureB2cSourceContext = null
	) {
		var targetSession = sessions[sessionIndex];
		var previousSessions = sessions.Take(sessionIndex).ToList();
		azureB2cSourceContext ??= BuildAzureB2cSourceContext(sessions);
		var isAzureB2cFlowSession = azureB2cSourceContext.FlowSessionIds.Contains(targetSession.SessionId);
		var unsourcedCookieList = BuildUnsourcedRequestCookies(targetSession, previousSessions, isAzureB2cFlowSession);
		RegisterUnsourcedCookies(unsourcedCookies, unsourcedCookieList);
		var requestForPlan = BuildRequestForCookieJar(targetSession.Request, unsourcedCookieList);
		var requestPlan = new RequestPlan(requestForPlan);
		PopulateReplacementSources(requestPlan, previousSessions, missing, isAzureB2cFlowSession);

		return new RequestSources(
			sessionIndex,
			targetSession.SessionId,
			targetSession.Request.Method,
			targetSession.Request.Url,
			requestPlan,
			null
		);
	}

	static void RegisterUnsourcedCookies(
		Dictionary<string, string>? unsourcedCookieDictionary,
		IReadOnlyList<UnsourcedRequestCookie> unsourcedCookies
	) {
		if (unsourcedCookieDictionary is null || unsourcedCookies.Count == 0)
			return;

		foreach (var unsourcedCookie in unsourcedCookies)
			RegisterDictionaryValue(unsourcedCookieDictionary, unsourcedCookie.Name, unsourcedCookie.Value);
	}

	static Request BuildRequestForCookieJar(Request original, IReadOnlyList<UnsourcedRequestCookie> unsourcedCookies) {
		var filteredCookies = unsourcedCookies
			.ToDictionary(cookie => cookie.Name, cookie => cookie.Value, StringComparer.OrdinalIgnoreCase);

		var filteredHeaders = original.Headers
			.Where(header => !string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
			.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);

		return original with {
			Cookies = filteredCookies,
			Headers = filteredHeaders,
		};
	}

	static List<UnsourcedRequestCookie> BuildUnsourcedRequestCookies(
		Session targetSession,
		IReadOnlyList<Session> previousSessions,
		bool isAzureB2cFlowSession
	) {
		var unsourced = new List<UnsourcedRequestCookie>();

		foreach (var cookie in targetSession.Request.Cookies.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) {
			if (string.IsNullOrWhiteSpace(cookie.Key) || string.IsNullOrWhiteSpace(cookie.Value))
				continue;

			if (isAzureB2cFlowSession && IsAzureB2cFlowCookie(cookie.Key))
				continue;

			if (HasCookieSource(previousSessions, cookie.Key, cookie.Value))
				continue;

			unsourced.Add(new UnsourcedRequestCookie(cookie.Key, cookie.Value));
		}

		return unsourced;
	}

	static bool IsAzureB2cFlowCookie(string cookieName) {
		foreach (var prefix in AzureB2cCookiePrefixes)
			if (cookieName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return true;

		return false;
	}

	static bool HasCookieSource(IReadOnlyList<Session> previousSessions, string cookieName, string cookieValue) {
		var pairNeedle = $"{cookieName}={cookieValue}";
		if (GetOrderedSources(previousSessions, pairNeedle).Count > 0)
			return true;

		if (GetOrderedSources(previousSessions, cookieValue).Count > 0)
			return true;

		return false;
	}

	static List<RequestSourceFinding> BuildRequestSourceFindings(
		Session targetSession,
		RequestPlan requestPlan,
		IReadOnlyList<Session> sessions,
		int sessionIndex,
		Dictionary<string, string>? missing = null
	) {
		var previousSessions = sessions.Take(sessionIndex).ToList();
		var findings = new List<RequestSourceFinding>();

		foreach (var piece in BuildRequestPieces(targetSession)) {
			if (piece.PieceKind == RequestSourcePieceKind.Path) {
				findings.AddRange(BuildPathSourceFindings(piece, previousSessions, missing));
				continue;
			}

			var orderedSources = GetOrderedSources(previousSessions, piece.Value);
			findings.Add(BuildFinding(piece, orderedSources.FirstOrDefault(), missing));
		}

		var isAzureB2cFlowSession = BuildAzureB2cSourceContext(sessions).FlowSessionIds.Contains(targetSession.SessionId);
		PopulateReplacementSources(requestPlan, previousSessions, missing, isAzureB2cFlowSession);

		return findings;
	}

	static void PopulateReplacementSources(
		RequestPlan requestPlan,
		IReadOnlyList<Session> previousSessions,
		Dictionary<string, string>? missing,
		bool isAzureB2cFlowSession
	) {
		foreach (var replacement in requestPlan.Replacements.Values) {
			var orderedSources = GetOrderedSources(previousSessions, replacement.OriginalValue);
			var source = orderedSources.FirstOrDefault();
			if (source is not null) {
				replacement.Source = source;
				continue;
			}

			if (isAzureB2cFlowSession && TryBuildAzureB2cSourceReference(replacement.Placeholder, out var azureB2cSource)) {
				replacement.Source = azureB2cSource;
				continue;
			}

			var missingKey = replacement.Placeholder;
			if (missing is not null) {
				missingKey = RegisterMissingValue(missing, replacement.Placeholder, replacement.OriginalValue);
			}

			replacement.Source = new MissingSourceReference(missingKey);
		}
	}

	static List<RequestSourceFinding> BuildPathSourceFindings(
		RequestPiece pathPiece,
		IReadOnlyList<Session> previousSessions,
		Dictionary<string, string>? missing
	) {
		var findings = new List<RequestSourceFinding>();
		var remaining = NormalizePathPart(pathPiece.Value);

		while (!string.IsNullOrWhiteSpace(remaining)) {
			var foundPath = FindPathMatchWithProgressiveTrim(remaining, previousSessions, out var source);
			if (string.IsNullOrWhiteSpace(foundPath) || source is null) {
				findings.Add(BuildFinding(new RequestPiece(pathPiece.PieceKind, pathPiece.Name, remaining), null, missing));
				break;
			}

			findings.Add(new RequestSourceFinding(pathPiece with {Value = foundPath }, source));

			if (string.Equals(foundPath, remaining, StringComparison.Ordinal))
				break;

			var unfound = remaining[foundPath.Length..];
			remaining = NormalizePathPart(unfound);
		}

		return findings;
	}

	static string? FindPathMatchWithProgressiveTrim(
		string path,
		IReadOnlyList<Session> previousSessions,
		out SourceFinding? source
	) {
		source = null;
		var candidate = NormalizePathPart(path);

		while (!string.IsNullOrWhiteSpace(candidate)) {
			var orderedSources = GetOrderedSources(previousSessions, candidate);
			source = orderedSources.FirstOrDefault();
			if (source is not null)
				return candidate;

			candidate = TrimDeepestSegment(candidate);
		}

		return null;
	}

	static List<SourceFinding> GetOrderedSources(IReadOnlyList<Session> previousSessions, string needle) {
		var sources = new HashSet<SourceFinding>();
		foreach (var previousSession in previousSessions)
			foreach (var source in FindSourcesInPreviousResponse(previousSession, needle))
				sources.Add(source);

		return sources
			.OrderBy(source => source.SessionId)
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

		var preferredKey = BuildPlaceholderName(piece);
		if (missing is not null) {
			var missingKey = RegisterMissingValue(missing, preferredKey, piece.Value);
			return new RequestSourceFinding(piece, new MissingSourceReference(missingKey));
		}

		return new RequestSourceFinding(piece, new MissingSourceReference(preferredKey));
	}

	static bool TryBuildAzureB2cSourceReference(string placeholder, out string sourceReference) {
		sourceReference = string.Empty;
		if (string.IsNullOrWhiteSpace(placeholder))
			return false;

		var trimmed = placeholder.Trim();
		if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal) && trimmed.Length > 2)
			trimmed = trimmed[1..^1];

		if (trimmed.Length == 0)
			return false;

		var key = StripNumericSuffix(trimmed);
		if (!AzureB2cFlowValueKeys.Contains(key))
			return false;

		sourceReference = $"{{AzureB2C:{key}}}";
		return true;
	}

	static string StripNumericSuffix(string value) {
		var lastUnderscore = value.LastIndexOf('_');
		if (lastUnderscore <= 0 || lastUnderscore >= value.Length - 1)
			return value;

		for (var i = lastUnderscore + 1; i < value.Length; i++)
			if (!char.IsDigit(value[i]))
				return value;

		return value[..lastUnderscore];
	}

	static string BuildPlaceholderName(RequestPiece piece) {
		var name = string.IsNullOrWhiteSpace(piece.Name) ? "value" : piece.Name.Trim();
		return $"{{{name}}}";
	}

	static string RegisterMissingValue(Dictionary<string, string> missing, string preferredKey, string value) {
		return RegisterDictionaryValue(missing, preferredKey, value);
	}

	static string RegisterDictionaryValue(Dictionary<string, string> dictionary, string preferredKey, string value) {
		var baseKey = string.IsNullOrWhiteSpace(preferredKey) ? "{value}" : preferredKey;

		if (!dictionary.TryGetValue(baseKey, out var existingValue)) {
			dictionary[baseKey] = value;
			return baseKey;
		}

		if (string.Equals(existingValue, value, StringComparison.Ordinal))
			return baseKey;

		var suffix = 1;
		while (true) {
			var collisionKey = AppendCollisionSuffix(baseKey, suffix);
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

		var trimmed = path.Trim();
		if (!trimmed.StartsWith("/", StringComparison.Ordinal))
			trimmed = "/" + trimmed;

		while (trimmed.Length > 1 && trimmed.EndsWith("/", StringComparison.Ordinal))
			trimmed = trimmed[..^1];


		return trimmed;
	}

	static string TrimDeepestSegment(string path) {
		if (string.IsNullOrWhiteSpace(path))
			return string.Empty;


		var normalized = NormalizePathPart(path);
		var lastSlash = normalized.LastIndexOf('/');
		if (lastSlash <= 0)
			return string.Empty;

		return normalized[..lastSlash];
	}

	static List<RequestPiece> BuildRequestPieces(Session targetSession) {
		var pieces = new List<RequestPiece>();

		if (!string.IsNullOrWhiteSpace(targetSession.Request.Host))
			pieces.Add(new RequestPiece(RequestSourcePieceKind.Host, "host", targetSession.Request.Host));

		var path = GetRequestPath(targetSession.Request);
		if (!string.IsNullOrWhiteSpace(path) && !string.Equals(path, "/", StringComparison.Ordinal))
			pieces.Add(new RequestPiece(RequestSourcePieceKind.Path, "path", path));

		foreach (var queryParameter in targetSession.Request.QueryParameters.OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase))
			if (!string.IsNullOrWhiteSpace(queryParameter.Value))
				pieces.Add(new RequestPiece(RequestSourcePieceKind.QueryParameter, queryParameter.Key, queryParameter.Value));

		if (string.Equals(targetSession.Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
			var bodyParameters = ExtractBodyParameters(targetSession.Request);
			foreach (var param in bodyParameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
				if (!string.IsNullOrWhiteSpace(param.Value))
					pieces.Add(new RequestPiece(RequestSourcePieceKind.BodyParameter, param.Key, param.Value));
		}

		return pieces;
	}

	static string GetRequestPath(Request request) {
		if (Uri.TryCreate(request.Url, UriKind.Absolute, out var absoluteUri))
			return absoluteUri.AbsolutePath;

		var target = request.Target.Split('?', 2)[0];
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
		foreach (var hint in request.Body.SchemaHints)
			if (!string.IsNullOrWhiteSpace(hint))
				parameters.Add(new FormBodyEntry(hint, hint));

		return parameters;
	}

	static IEnumerable<SourceFinding> FindSourcesInPreviousResponse(Session previousSession, string needle) {
		if (string.IsNullOrWhiteSpace(needle))
			yield break;

		if (!string.IsNullOrWhiteSpace(previousSession.Request.Host) && ContainsInsensitive(previousSession.Request.Host, needle))
			yield return new SourceFinding(previousSession.SessionId, "request host", "host", needle);

		foreach (var header in previousSession.Response.Headers.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase))
			if (ContainsInsensitive(header.Value, needle))
				yield return new SourceFinding(previousSession.SessionId, "response header", header.Key, needle);

		if (!string.IsNullOrWhiteSpace(previousSession.Response.ResponseText) && ContainsInsensitive(previousSession.Response.ResponseText, needle))
			yield return new SourceFinding(previousSession.SessionId, "response body text", null, needle);

		if (previousSession.Response.ResponseJson is JsonElement responseJson)
			foreach (var match in FindJsonValueMatches(responseJson, needle, "$"))
				yield return new SourceFinding(previousSession.SessionId, "response JSON", match.Path, needle);

	}

	static IEnumerable<JsonValueMatch> FindJsonValueMatches(JsonElement element, string needle, string path) {
		switch (element.ValueKind) {
			case JsonValueKind.Object:
				foreach (var property in element.EnumerateObject()) {
					var propertyPath = path == "$" ? $"$.{property.Name}" : $"{path}.{property.Name}";
					foreach (var match in FindJsonValueMatches(property.Value, needle, propertyPath)) {
						yield return match;
					}
				}
				break;
			case JsonValueKind.Array:
				var index = 0;
				foreach (var item in element.EnumerateArray()) {
					var itemPath = $"{path}[{index++}]";
					foreach (var match in FindJsonValueMatches(item, needle, itemPath)) {
						yield return match;
					}
				}
				break;
			case JsonValueKind.String:
				var stringValue = element.GetString() ?? string.Empty;
				if (ContainsInsensitive(stringValue, needle)) {
					yield return new JsonValueMatch(path, stringValue);
				}
				break;
			case JsonValueKind.Number:
			case JsonValueKind.True:
			case JsonValueKind.False:
				var scalarValue = element.ToString();
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
	HashSet<int> FlowSessionIds
);
