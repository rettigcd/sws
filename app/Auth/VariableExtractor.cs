using System.Text.Json;

namespace Auth;

/// <summary>
/// Extracts every variable participating in a detected flow and classifies it into one of the
/// six categories described in docs/AUTH_FLOW_DETECTION_SPEC.md. Per spec, nothing is redacted.
/// </summary>
internal static class VariableExtractor {

	static readonly HashSet<string> ConfigurationKeys = new(StringComparer.OrdinalIgnoreCase) {
		"client_id", "redirect_uri", "scope", "authority", "tenant", "policy",
		"response_type", "resource", "audience", "response_mode", "prompt",
		"login_hint", "domain_hint", "ui_locales", "p",
	};

	static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase) {
		"client_secret", "client_assertion",
	};

	static readonly HashSet<string> ServerGeneratedKeys = new(StringComparer.OrdinalIgnoreCase) {
		"code", "access_token", "id_token", "refresh_token", "session_state",
		"device_code", "user_code", "error", "error_description",
	};

	static readonly HashSet<string> DerivedKeys = new(StringComparer.OrdinalIgnoreCase) {
		"code_challenge",
	};

	static readonly HashSet<string> GeneratedPerFlowKeys = new(StringComparer.OrdinalIgnoreCase) {
		"state", "nonce", "code_verifier", "correlation_id", "request_id",
		"client-request-id", "client_info", "code_challenge_method", "grant_type",
	};

	static readonly string[] NonParticipatingHints = [
		"utm_", "_ga", "_gid", "ai_user", "ai_session", "optanonconsent", "optanonalertboxclosed",
	];

	public static List<Variable> Extract(FlowInProgress flow, IReadOnlyList<Session> allSessions) {
		var variables = new List<Variable>();
		var flowSessions = allSessions.Where(s => flow.RelatedSessionIds.Contains(s.SessionId));

		foreach (var session in flowSessions) {
			ExtractFromRequest(session, variables);
			ExtractFromResponse(session, variables);
		}

		return variables;
	}

	static void ExtractFromRequest(Session session, List<Variable> variables) {
		foreach (var kvp in session.Request.QueryParameters)
			Add(variables, kvp.Key, kvp.Value, VariableSource.QueryParameter, session.SessionId);

		if (session.Request.FormBody is { Count: > 0 })
			foreach (var entry in session.Request.FormBody)
				Add(variables, entry.Key, entry.Value, VariableSource.FormField, session.SessionId);

		foreach (var kvp in session.Request.Headers)
			Add(variables, kvp.Key, kvp.Value, VariableSource.RequestHeader, session.SessionId);

		foreach (var kvp in session.Request.Cookies)
			Add(variables, kvp.Key, kvp.Value, VariableSource.Cookie, session.SessionId);

		if (OAuthParameterHelpers.TryParseFragmentParameters(session.Request.Fragment, out var fragmentParams))
			foreach (var kvp in fragmentParams)
				Add(variables, kvp.Key, kvp.Value, VariableSource.FragmentParameter, session.SessionId);
	}

	static void ExtractFromResponse(Session session, List<Variable> variables) {
		foreach (var kvp in session.Response.Headers) {
			var source = kvp.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ? VariableSource.SetCookie : VariableSource.ResponseHeader;
			Add(variables, kvp.Key, kvp.Value, source, session.SessionId);
		}

		if (session.Response.ResponseJson is { ValueKind: JsonValueKind.Object } json)
			foreach (var property in json.EnumerateObject())
				if (TryGetScalarString(property.Value, out string value))
					Add(variables, property.Name, value, VariableSource.JsonBodyField, session.SessionId, jsonPath: $"$.{property.Name}");

		if (session.Response.Headers.TryGetValue("Location", out string? location) && Uri.TryCreate(location, UriKind.Absolute, out var locationUri)) {
			foreach (var pair in ParseQuery(locationUri.Query))
				Add(variables, pair.Key, pair.Value, VariableSource.RedirectUrlParameter, session.SessionId);

			if (OAuthParameterHelpers.TryParseFragmentParameters(locationUri.Fragment, out var locationFragmentParams))
				foreach (var kvp in locationFragmentParams)
					Add(variables, kvp.Key, kvp.Value, VariableSource.RedirectUrlParameter, session.SessionId);
		}
	}

	static void Add(List<Variable> variables, string name, string value, VariableSource source, int sessionId, string? jsonPath = null) {
		if (string.IsNullOrWhiteSpace(name))
			return;

		var category = Classify(name, value, source);
		string? derivedFrom = category == VariableCategory.Derived ? "code_verifier" : null;
		variables.Add(new Variable(name, value, category, source, sessionId, jsonPath, derivedFrom));
	}

	static VariableCategory Classify(string name, string value, VariableSource source) {
		if (source == VariableSource.RequestHeader && name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
			return VariableCategory.Secret;

		if (SecretKeys.Contains(name))
			return VariableCategory.Secret;

		if (ServerGeneratedKeys.Contains(name))
			return VariableCategory.ServerGenerated;

		if (source is VariableSource.SetCookie && AzureB2c.B2cCookieNames.IsB2cCookie(name))
			return VariableCategory.ServerGenerated;

		if (DerivedKeys.Contains(name))
			return VariableCategory.Derived;

		if (GeneratedPerFlowKeys.Contains(name))
			return VariableCategory.GeneratedPerFlow;

		if (source is VariableSource.Cookie or VariableSource.SetCookie && AzureB2c.B2cCookieNames.IsB2cCookie(name))
			return VariableCategory.ServerGenerated;

		if (ConfigurationKeys.Contains(name))
			return VariableCategory.Configuration;

		foreach (string hint in NonParticipatingHints)
			if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
				return VariableCategory.NonParticipating;

		return VariableCategory.NonParticipating;
	}

	static bool TryGetScalarString(JsonElement element, out string value) {
		switch (element.ValueKind) {
			case JsonValueKind.String:
				value = element.GetString() ?? string.Empty;
				return true;
			case JsonValueKind.Number:
			case JsonValueKind.True:
			case JsonValueKind.False:
				value = element.ToString();
				return true;
			default:
				value = string.Empty;
				return false;
		}
	}

	static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query) {
		if (string.IsNullOrWhiteSpace(query))
			yield break;

		foreach (string piece in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = piece.Split('=', 2);
			string key = Uri.UnescapeDataString(parts[0]);
			if (string.IsNullOrWhiteSpace(key))
				continue;

			string value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
			yield return new KeyValuePair<string, string>(key, value);
		}
	}
}
