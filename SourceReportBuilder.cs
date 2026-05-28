using System.Text.Json;

internal static class SourceReportBuilder
{
	public static SessionSourcesReport BuildSessionSourcesReport(
		int sessionIndex,
		IReadOnlyList<SessionPlan> sessions,
		List<string>? missing = null,
		Dictionary<string, int>? missingIndexes = null)
	{
		var targetSession = sessions[sessionIndex - 1];
		return new SessionSourcesReport(
			sessionIndex,
			targetSession.SessionId,
			targetSession.Request.Method,
			targetSession.Request.Url,
			BuildRequestSourceFindings(targetSession, sessions, sessionIndex, missing, missingIndexes)
		);
	}

	private static List<RequestSourceFinding> BuildRequestSourceFindings(
		SessionPlan targetSession,
		IReadOnlyList<SessionPlan> sessions,
		int sessionIndex,
		List<string>? missing = null,
		Dictionary<string, int>? missingIndexes = null)
	{
		var previousSessions = sessions.Take(sessionIndex - 1).ToList();
		var findings = new List<RequestSourceFinding>();

		foreach (var piece in BuildRequestPieces(targetSession))
		{
			var sources = new HashSet<SourceFinding>();
			foreach (var previousSession in previousSessions)
			{
				foreach (var source in FindSourcesInPreviousResponse(previousSession, piece.Value))
				{
					sources.Add(source);
				}
			}

			var orderedSources = sources
				.OrderBy(source => source.SessionId)
				.ThenBy(source => source.SourceKind, StringComparer.OrdinalIgnoreCase)
				.ThenBy(source => source.SourceName, StringComparer.OrdinalIgnoreCase)
				.ThenBy(source => source.Needle, StringComparer.OrdinalIgnoreCase)
				.ToList();

			var firstSource = orderedSources.FirstOrDefault();
			if (firstSource is not null)
			{
				findings.Add(new RequestSourceFinding(piece.PieceKind, piece.Name, piece.Value, firstSource));
				continue;
			}

			if (missing is not null && missingIndexes is not null)
			{
				if (!missingIndexes.TryGetValue(piece.Value, out var missingIndex))
				{
					missingIndex = missing.Count;
					missing.Add(piece.Value);
					missingIndexes[piece.Value] = missingIndex;
				}

				findings.Add(new RequestSourceFinding(piece.PieceKind, piece.Name, piece.Value, new MissingSourceReference(missingIndex)));
				continue;
			}

			findings.Add(new RequestSourceFinding(piece.PieceKind, piece.Name, piece.Value, new MissingSourceReference(-1)));
		}

		return findings;
	}

	private static List<RequestPiece> BuildRequestPieces(SessionPlan targetSession)
	{
		var pieces = new List<RequestPiece>();

		if (!string.IsNullOrWhiteSpace(targetSession.Request.Host))
		{
			pieces.Add(new RequestPiece("host", "host", targetSession.Request.Host));
		}

		var path = GetRequestPath(targetSession.Request);
		if (!string.IsNullOrWhiteSpace(path) && !string.Equals(path, "/", StringComparison.Ordinal))
		{
			pieces.Add(new RequestPiece("path", "path", path));
		}

		foreach (var queryParameter in targetSession.Request.QueryParameters.OrderBy(parameter => parameter.Key, StringComparer.OrdinalIgnoreCase))
		{
			if (!string.IsNullOrWhiteSpace(queryParameter.Value))
			{
				pieces.Add(new RequestPiece("query-parameter", queryParameter.Key, queryParameter.Value));
			}
		}

		if (string.Equals(targetSession.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
		{
			var bodyParameters = ExtractBodyParameters(targetSession.Request);
			foreach (var param in bodyParameters.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
			{
				if (!string.IsNullOrWhiteSpace(param.Value))
				{
					pieces.Add(new RequestPiece("body-parameter", param.Key, param.Value));
				}
			}
		}

		return pieces;
	}

	private static string GetRequestPath(RequestPlan request)
	{
		if (Uri.TryCreate(request.Url, UriKind.Absolute, out var absoluteUri))
		{
			return absoluteUri.AbsolutePath;
		}

		var target = request.Target.Split('?', 2)[0];
		return target;
	}

	private static List<FormBodyEntry> ExtractBodyParameters(RequestPlan request)
	{
		var parameters = new List<FormBodyEntry>();

		if (request.FormBody is { Count: > 0 })
		{
			foreach (var pair in request.FormBody)
			{
				if (!string.IsNullOrWhiteSpace(pair.Key))
				{
					parameters.Add(pair);
				}
			}

			return parameters;
		}

		if (request.Body.Length == 0 || string.IsNullOrWhiteSpace(request.Body.Format))
		{
			return parameters;
		}

		// Fallback for non-form bodies where parsed form pairs are unavailable.
		foreach (var hint in request.Body.SchemaHints)
		{
			if (!string.IsNullOrWhiteSpace(hint))
			{
				parameters.Add(new FormBodyEntry(hint, hint));
			}
		}

		return parameters;
	}

	private static IEnumerable<SourceFinding> FindSourcesInPreviousResponse(SessionPlan previousSession, string needle)
	{
		if (string.IsNullOrWhiteSpace(needle))
		{
			yield break;
		}

		if (!string.IsNullOrWhiteSpace(previousSession.Request.Host) && ContainsInsensitive(previousSession.Request.Host, needle))
		{
			yield return new SourceFinding(previousSession.SessionId, "request host", "host", needle);
		}

		foreach (var header in previousSession.Response.Headers.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase))
		{
			if (ContainsInsensitive(header.Value, needle))
			{
				yield return new SourceFinding(previousSession.SessionId, "response header", header.Key, needle);
			}
		}

		if (!string.IsNullOrWhiteSpace(previousSession.Response.ResponseText) && ContainsInsensitive(previousSession.Response.ResponseText, needle))
		{
			yield return new SourceFinding(previousSession.SessionId, "response body text", null, needle);
		}

		if (previousSession.Response.ResponseJson is JsonElement responseJson)
		{
			foreach (var match in FindJsonValueMatches(responseJson, needle, "$"))
			{
				yield return new SourceFinding(previousSession.SessionId, "response JSON", match.Path, needle);
			}
		}
	}

	private static IEnumerable<JsonValueMatch> FindJsonValueMatches(JsonElement element, string needle, string path)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (var property in element.EnumerateObject())
				{
					var propertyPath = path == "$" ? $"$.{property.Name}" : $"{path}.{property.Name}";
					foreach (var match in FindJsonValueMatches(property.Value, needle, propertyPath))
					{
						yield return match;
					}
				}
				break;
			case JsonValueKind.Array:
				var index = 0;
				foreach (var item in element.EnumerateArray())
				{
					var itemPath = $"{path}[{index++}]";
					foreach (var match in FindJsonValueMatches(item, needle, itemPath))
					{
						yield return match;
					}
				}
				break;
			case JsonValueKind.String:
				var stringValue = element.GetString() ?? string.Empty;
				if (ContainsInsensitive(stringValue, needle))
				{
					yield return new JsonValueMatch(path, stringValue);
				}
				break;
			case JsonValueKind.Number:
			case JsonValueKind.True:
			case JsonValueKind.False:
				var scalarValue = element.ToString();
				if (ContainsInsensitive(scalarValue, needle))
				{
					yield return new JsonValueMatch(path, scalarValue);
				}
				break;
		}
	}

	private static bool ContainsInsensitive(string haystack, string needle)
	{
		return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
	}

	private sealed record RequestPiece(
		string PieceKind,
		string Name,
		string Value
	);

	private sealed record JsonValueMatch(
		string Path,
		string Value
	);
}
