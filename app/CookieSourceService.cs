internal static class CookieSourceService {
	static readonly string[] AzureB2cCookiePrefixes =
	[
		"x-ms-cpim-",
	];

	public static List<UnsourcedRequestCookie> BuildUnsourcedRequestCookies(
		Session targetSession,
		IReadOnlyList<Session> previousSessions,
		bool isAzureB2cFlowSession,
		Func<IReadOnlyList<Session>, string, List<SourceFinding>> getOrderedSources
	) {
		var unsourced = new List<UnsourcedRequestCookie>();

		foreach (var cookie in targetSession.Request.Cookies.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) {
			if (string.IsNullOrWhiteSpace(cookie.Key) || string.IsNullOrWhiteSpace(cookie.Value))
				continue;

			if (isAzureB2cFlowSession && IsAzureB2cFlowCookie(cookie.Key))
				continue;

			if (HasCookieSource(previousSessions, cookie.Key, cookie.Value, getOrderedSources))
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
		foreach (var prefix in AzureB2cCookiePrefixes)
			if (cookieName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return true;

		return false;
	}

	static bool HasCookieSource(
		IReadOnlyList<Session> previousSessions,
		string cookieName,
		string cookieValue,
		Func<IReadOnlyList<Session>, string, List<SourceFinding>> getOrderedSources
	) {
		var pairNeedle = $"{cookieName}={cookieValue}";
		if (getOrderedSources(previousSessions, pairNeedle).Count > 0)
			return true;

		if (getOrderedSources(previousSessions, cookieValue).Count > 0)
			return true;

		return false;
	}
}
