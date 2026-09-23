namespace Analysis;

using Saz;

/// <summary>Finds the responses of earlier exchanges that carried a given value.</summary>
internal static class SourceLocator {

	const int MinSearchLength = 3;

	/// <summary>
	/// Whether an input is worth searching for at all. Values shorter than <see cref="MinSearchLength"/> would match
	/// almost anything, and route words in a path (".../booking-widget/series/...") are structure, not data.
	/// </summary>
	public static bool IsSearchable(FlowInput input) {
		if (input.Value.Length < MinSearchLength)
			return false;

		return input.Kind != InputKind.PathSegment || LooksLikeIdentifier(input.Value);
	}

	/// <summary>An ID or token (random-looking, with at least one digit) rather than a route word or a hyphenated phrase.</summary>
	public static bool LooksLikeIdentifier(string value) {
		return InterestingFinder.IsInteresting(value) && value.Any(char.IsDigit);
	}

	/// <summary>
	/// Every exchange before <paramref name="consumerIndex"/> whose response carried the value, earliest first
	/// (one location per exchange). ID/token-like values match anywhere as a whole token; plain values only match
	/// a whole cookie value, header value or JSON value.
	/// </summary>
	public static List<SourceLocation> Find(IReadOnlyList<Exchange> exchanges, int consumerIndex, FlowInput input) {
		var locations = new List<SourceLocation>();
		if (!IsSearchable(input))
			return locations;

		bool wholeTokenMatch = LooksLikeIdentifier(input.Value);
		for (int i = 0; i < consumerIndex; i++) {
			string? where = FindInResponse(exchanges[i].Response, input.Value, wholeTokenMatch);
			if (where is not null)
				locations.Add(new SourceLocation(exchanges[i].ExchangeId, i, where));
		}

		return locations;
	}

	static string? FindInResponse(Response response, string value, bool wholeTokenMatch) {
		foreach (var header in response.Headers) {
			if (string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase)) {
				foreach (var (name, cookieValue) in ParseSetCookies(header.Value))
					if (cookieValue == value || (wholeTokenMatch && ContainsToken(cookieValue, value)))
						return $"Set-Cookie: {name}";
			}
			else if (header.Value == value || (wholeTokenMatch && ContainsToken(header.Value, value))) {
				return $"response header: {header.Key}";
			}
		}

		if (response.ResponseJson is { } json)
			foreach (var leaf in JsonLeaves.Enumerate(json))
				if (leaf.Value == value || (wholeTokenMatch && ContainsToken(leaf.Value, value)))
					return $"response JSON: {leaf.Path}";

		if (wholeTokenMatch && response.ResponseText is { Length: > 0 } text && ContainsToken(text, value))
			return "response body text";

		return null;
	}

	/// <summary>Splits a (possibly multi-line) Set-Cookie header value into cookie name/value pairs.</summary>
	static IEnumerable<(string Name, string Value)> ParseSetCookies(string headerValue) {
		foreach (string line in headerValue.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
			string pair = line.Split(';', 2)[0];
			int equals = pair.IndexOf('=');
			if (equals > 0)
				yield return (pair[..equals].Trim(), pair[(equals + 1)..].Trim());
		}
	}

	/// <summary>True if <paramref name="value"/> occurs in <paramref name="haystack"/> not glued to other letters or digits.</summary>
	static bool ContainsToken(string haystack, string value) {
		int start = 0;
		while ((start = haystack.IndexOf(value, start, StringComparison.Ordinal)) >= 0) {
			int end = start + value.Length;
			bool boundedBefore = start == 0 || !char.IsLetterOrDigit(haystack[start - 1]);
			bool boundedAfter = end == haystack.Length || !char.IsLetterOrDigit(haystack[end]);
			if (boundedBefore && boundedAfter)
				return true;

			start++;
		}

		return false;
	}
}
