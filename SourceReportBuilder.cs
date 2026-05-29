using System.Text.Json;

internal static class SourceReportBuilder {

	public static SessionSourcesReport BuildSessionSourcesReport(
		int sessionIndex,
		IReadOnlyList<Session> sessions,
		List<string>? missing = null,
		Dictionary<string, int>? missingIndexes = null
	) {
		var targetSession = sessions[sessionIndex];

		return new SessionSourcesReport(
			sessionIndex,
			targetSession.SessionId,
			targetSession.Request.Method,
			targetSession.Request.Url,
			BuildRequestSourceFindings(targetSession, sessions, sessionIndex, missing, missingIndexes)
		);
	}

	static List<RequestSourceFinding> BuildRequestSourceFindings(
		Session targetSession,
		IReadOnlyList<Session> sessions,
		int sessionIndex,
		List<string>? missing = null,
		Dictionary<string, int>? missingIndexes = null
	) {
		var previousSessions = sessions.Take(sessionIndex).ToList();
		var findings = new List<RequestSourceFinding>();

		foreach (var piece in BuildRequestPieces(targetSession)) {
			if (piece.PieceKind == RequestPieceKind.Path) {
				findings.AddRange(BuildPathSourceFindings(piece, previousSessions, missing, missingIndexes));
				continue;
			}

			var orderedSources = GetOrderedSources(previousSessions, piece.Value);
			findings.Add(BuildFinding(piece, orderedSources.FirstOrDefault(), missing, missingIndexes));
		}

		return findings;
	}

	static List<RequestSourceFinding> BuildPathSourceFindings(
		RequestPiece pathPiece,
		IReadOnlyList<Session> previousSessions,
		List<string>? missing,
		Dictionary<string, int>? missingIndexes
	) {
		var findings = new List<RequestSourceFinding>();
		var remaining = NormalizePathPart(pathPiece.Value);

		while (!string.IsNullOrWhiteSpace(remaining)) {
			var foundPath = FindPathMatchWithProgressiveTrim(remaining, previousSessions, out var source);
			if (string.IsNullOrWhiteSpace(foundPath) || source is null) {
				findings.Add(BuildFinding(new RequestPiece(pathPiece.PieceKind, pathPiece.Name, remaining), null, missing, missingIndexes));
				break;
			}

			findings.Add(new RequestSourceFinding(ToPieceKindName(pathPiece.PieceKind), pathPiece.Name, foundPath, source));

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
		List<string>? missing,
		Dictionary<string, int>? missingIndexes
	) {
		if (source is not null)
			return new RequestSourceFinding(ToPieceKindName(piece.PieceKind), piece.Name, piece.Value, source);

		if (missing is not null && missingIndexes is not null) {
			if (!missingIndexes.TryGetValue(piece.Value, out var missingIndex)) {
				missingIndex = missing.Count;
				missing.Add(piece.Value);
				missingIndexes[piece.Value] = missingIndex;
			}

			return new RequestSourceFinding(ToPieceKindName(piece.PieceKind), piece.Name, piece.Value, new MissingSourceReference(missingIndex));
		}

		return new RequestSourceFinding(ToPieceKindName(piece.PieceKind), piece.Name, piece.Value, new MissingSourceReference(-1));
	}

	static string ToPieceKindName(RequestPieceKind pieceKind) {
		return pieceKind switch {
			RequestPieceKind.Host => "host",
			RequestPieceKind.Path => "path",
			RequestPieceKind.QueryParameter => "query-parameter",
			RequestPieceKind.BodyParameter => "body-parameter",
			_ => throw new ArgumentOutOfRangeException(nameof(pieceKind), pieceKind, "Unsupported request piece kind."),
		};
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
			pieces.Add(new RequestPiece(RequestPieceKind.Host, "host", targetSession.Request.Host));

		var path = GetRequestPath(targetSession.Request);
		if (!string.IsNullOrWhiteSpace(path) && !string.Equals(path, "/", StringComparison.Ordinal))
			pieces.Add(new RequestPiece(RequestPieceKind.Path, "path", path));

		foreach (var queryParameter in targetSession.Request.QueryParameters.OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase))
			if (!string.IsNullOrWhiteSpace(queryParameter.Value))
				pieces.Add(new RequestPiece(RequestPieceKind.QueryParameter, queryParameter.Key, queryParameter.Value));

		if (string.Equals(targetSession.Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
			var bodyParameters = ExtractBodyParameters(targetSession.Request);
			foreach (var param in bodyParameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
				if (!string.IsNullOrWhiteSpace(param.Value))
					pieces.Add(new RequestPiece(RequestPieceKind.BodyParameter, param.Key, param.Value));
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

	enum RequestPieceKind {
		Host,
		Path,
		QueryParameter,
		BodyParameter,
	}

	sealed record RequestPiece(
		RequestPieceKind PieceKind,
		string Name,
		string Value
	);

	sealed record JsonValueMatch(
		string Path,
		string Value
	);
}
