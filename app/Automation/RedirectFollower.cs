namespace Automation;

/// <summary>
/// Redirect-hop mechanics shared by flow handlers. All 3xx hops are followed as GET with no
/// body (matches real-world B2C/OIDC redirect chains, which use 302 for every interstitial
/// hop) rather than implementing full 307/308 method/body preservation, which this domain
/// does not exercise in practice.
/// </summary>
internal static class RedirectFollower {

	public static bool MatchesRedirectUri(Uri location, string redirectUri) {
		return Uri.TryCreate(redirectUri, UriKind.Absolute, out var parsed)
			&& string.Equals(location.GetLeftPart(UriPartial.Path), parsed.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Extracts callback parameters from either the query string or fragment, preferring
	/// whichever the flow's response_mode indicates, falling back to the other if empty.
	/// </summary>
	public static Dictionary<string, string> ExtractCallbackParameters(Uri location, bool preferFragment) {
		string primary = preferFragment ? location.Fragment.TrimStart('#') : location.Query.TrimStart('?');
		var parameters = ParseFormEncoded(primary);
		if (parameters.Count > 0)
			return parameters;

		string secondary = preferFragment ? location.Query.TrimStart('?') : location.Fragment.TrimStart('#');
		return ParseFormEncoded(secondary);
	}

	static Dictionary<string, string> ParseFormEncoded(string raw) {
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string piece in raw.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = piece.Split('=', 2);
			string key = Uri.UnescapeDataString(parts[0]);
			if (string.IsNullOrWhiteSpace(key))
				continue;

			result[key] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
		}

		return result;
	}
}
