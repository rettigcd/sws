namespace Analysis;

using Saz;

internal static class CookieSourceService {
	static readonly string[] AzureB2cCookiePrefixes =
	[
		"x-ms-cpim-",
	];

	public static List<UnsourcedRequestCookie> BuildUnsourcedRequestCookies(
		Exchange targetExchange,
		IReadOnlyList<Exchange> previousExchanges,
		bool isAzureB2cFlowExchange,
		Func<IReadOnlyList<Exchange>, string, List<SourceFinding>> getOrderedSources
	) {
		var unsourced = new List<UnsourcedRequestCookie>();

		foreach (var cookie in targetExchange.Request.Cookies.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) {
			if (string.IsNullOrWhiteSpace(cookie.Key) || string.IsNullOrWhiteSpace(cookie.Value))
				continue;

			if (isAzureB2cFlowExchange && IsAzureB2cFlowCookie(cookie.Key))
				continue;

			if (HasCookieSource(previousExchanges, cookie.Key, cookie.Value, getOrderedSources))
				continue;

			unsourced.Add(new UnsourcedRequestCookie(cookie.Key, cookie.Value));
		}

		return unsourced;
	}

	public static void RegisterUnsourcedCookies(
		Dictionary<string, string>? unsourcedCookieDictionary,
		IReadOnlyList<UnsourcedRequestCookie> unsourcedCookies,
		Func<Dictionary<string, string>, string, string, string> registerDictionaryValue
	) {
		if (unsourcedCookieDictionary is null || unsourcedCookies.Count == 0)
			return;

		foreach (var unsourcedCookie in unsourcedCookies)
			registerDictionaryValue(unsourcedCookieDictionary, unsourcedCookie.Name, unsourcedCookie.Value);
	}

	public static Request BuildRequestForCookieJar(Request original, IReadOnlyList<UnsourcedRequestCookie> unsourcedCookies) {
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

	static bool IsAzureB2cFlowCookie(string cookieName) {
		foreach (string prefix in AzureB2cCookiePrefixes)
			if (cookieName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return true;

		return false;
	}

	static bool HasCookieSource(
		IReadOnlyList<Exchange> previousExchanges,
		string cookieName,
		string cookieValue,
		Func<IReadOnlyList<Exchange>, string, List<SourceFinding>> getOrderedSources
	) {
		string pairNeedle = $"{cookieName}={cookieValue}";
		if (getOrderedSources(previousExchanges, pairNeedle).Count > 0)
			return true;

		if (getOrderedSources(previousExchanges, cookieValue).Count > 0)
			return true;

		return false;
	}
}
