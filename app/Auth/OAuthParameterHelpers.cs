namespace Auth;

/// <summary>
/// Low-level parameter/fragment/URL helpers shared by session classification and flow correlation.
/// Ported near-verbatim from the original Azure-specific scanner; these were already generic.
/// </summary>
internal static class OAuthParameterHelpers {

	public static bool IsOauth2AuthorizationRequest(Request request) {
		return request.QueryParameters.ContainsKey("response_type")
			&& request.QueryParameters.ContainsKey("client_id");
	}

	public static bool HasResponseType(string responseTypesRaw, string expected) {
		return responseTypesRaw
			.Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Any(value => value.Equals(expected, StringComparison.OrdinalIgnoreCase));
	}

	public static bool TryGetRequestParameter(Request request, string key, out string value) {
		if (request.QueryParameters.TryGetValue(key, out var queryValue) && queryValue is not null) {
			value = queryValue;
			return true;
		}

		if (request.FormBody is { Count: > 0 }) {
			var entry = request.FormBody
				.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

			if (entry is not null) {
				value = entry.Value;
				return true;
			}
		}

		value = string.Empty;
		return false;
	}

	public static bool TryParseFragmentParameters(string? fragment, out Dictionary<string, string> parameters) {
		parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(fragment))
			return false;

		var raw = fragment.TrimStart('#');
		if (raw.Length == 0)
			return false;

		foreach (var piece in raw.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = piece.Split('=', 2);
			var key = Uri.UnescapeDataString(parts[0]);
			if (string.IsNullOrWhiteSpace(key))
				continue;

			var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
			parameters[key] = value;
		}

		return parameters.Count != 0;
	}

	public static bool HasCodeAndState(IReadOnlyDictionary<string, string> parameters) {
		return parameters.ContainsKey("code")
			&& (parameters.ContainsKey("state") || parameters.ContainsKey("session_state"));
	}

	public static bool UrlsMatchIgnoringFragment(string left, string right) {
		if (Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
			&& Uri.TryCreate(right, UriKind.Absolute, out var rightUri)) {
			return leftUri.GetLeftPart(UriPartial.Path).Equals(rightUri.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase)
				&& string.Equals(leftUri.Query, rightUri.Query, StringComparison.OrdinalIgnoreCase);
		}

		var normalizedLeft = left.Split('#', 2)[0];
		var normalizedRight = right.Split('#', 2)[0];
		return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
	}

	public static bool UrlsMatchIgnoringFragmentAndQuery(string left, string right) {
		if (Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
			&& Uri.TryCreate(right, UriKind.Absolute, out var rightUri)) {
			return leftUri.GetLeftPart(UriPartial.Path).Equals(rightUri.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase);
		}

		var normalizedLeft = left.Split(['#', '?'], 2)[0];
		var normalizedRight = right.Split(['#', '?'], 2)[0];
		return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
	}

	public static bool TryParseFragmentFromLocation(Response response, string callbackUrl, out Dictionary<string, string> parameters) {
		parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (!response.Headers.TryGetValue("Location", out var location)
			|| string.IsNullOrWhiteSpace(location)
			|| !UrlsMatchIgnoringFragment(location, callbackUrl)) {
			return false;
		}

		if (!Uri.TryCreate(location, UriKind.Absolute, out var locationUri))
			return false;

		return TryParseFragmentParameters(locationUri.Fragment, out parameters);
	}

	public static bool ContainsPath(string url, string marker) {
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			return url.Contains(marker, StringComparison.OrdinalIgnoreCase);

		return uri.AbsolutePath.Contains(marker, StringComparison.OrdinalIgnoreCase);
	}
}
