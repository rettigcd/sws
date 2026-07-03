namespace Auth;

/// <summary>
/// Classifies URLs as OIDC/OAuth2 endpoint types. Generic path-based matching first
/// (works for any provider), refined by an optional discovery document when one was observed.
/// </summary>
internal static class EndpointClassifier {

	public static bool IsAuthorizeRequest(string url, OidcDiscoveryDocument? discovery = null) {
		if (discovery?.AuthorizationEndpoint is { Length: > 0 } endpoint
			&& OAuthParameterHelpers.UrlsMatchIgnoringFragmentAndQuery(url, endpoint))
			return true;

		return OAuthParameterHelpers.ContainsPath(url, "/authorize");
	}

	public static bool IsTokenRequest(string url, OidcDiscoveryDocument? discovery = null) {
		if (discovery?.TokenEndpoint is { Length: > 0 } endpoint
			&& OAuthParameterHelpers.UrlsMatchIgnoringFragmentAndQuery(url, endpoint))
			return true;

		return OAuthParameterHelpers.ContainsPath(url, "/token");
	}

	public static bool IsOpenIdConfiguration(string url) {
		return OAuthParameterHelpers.ContainsPath(url, "/.well-known/openid-configuration")
			|| OAuthParameterHelpers.ContainsPath(url, "/.well-known/oauth-authorization-server");
	}

	public static bool IsDeviceAuthorizationEndpoint(string url, OidcDiscoveryDocument? discovery = null) {
		if (discovery?.DeviceAuthorizationEndpoint is { Length: > 0 } endpoint
			&& OAuthParameterHelpers.UrlsMatchIgnoringFragmentAndQuery(url, endpoint))
			return true;

		return OAuthParameterHelpers.ContainsPath(url, "/devicecode")
			|| OAuthParameterHelpers.ContainsPath(url, "/device_authorization")
			|| OAuthParameterHelpers.ContainsPath(url, "/device/code");
	}

	public static bool IsEndSessionEndpoint(string url, OidcDiscoveryDocument? discovery = null) {
		if (discovery?.EndSessionEndpoint is { Length: > 0 } endpoint
			&& OAuthParameterHelpers.UrlsMatchIgnoringFragmentAndQuery(url, endpoint))
			return true;

		return OAuthParameterHelpers.ContainsPath(url, "/logout")
			|| OAuthParameterHelpers.ContainsPath(url, "/endsession")
			|| OAuthParameterHelpers.ContainsPath(url, "/end_session");
	}

	/// <summary>
	/// Finds the discovery document (if any) most relevant to a session: the nearest preceding
	/// discovery response on the same host, falling back to any preceding discovery response.
	/// </summary>
	public static OidcDiscoveryDocument? FindRelevantDiscovery(Session session, IReadOnlyList<Session> priorSessions) {
		OidcDiscoveryDocument? anyPriorDiscovery = null;
		var host = TryGetHost(session.Request.Url);

		for (var i = priorSessions.Count - 1; i >= 0; i--) {
			var candidate = priorSessions[i];
			if (!IsOpenIdConfiguration(candidate.Request.Url))
				continue;

			var discovery = DiscoveryDocumentParser.TryParse(candidate);
			if (discovery is null)
				continue;

			anyPriorDiscovery ??= discovery;

			if (host is not null && TryGetHost(candidate.Request.Url) == host)
				return discovery;
		}

		return anyPriorDiscovery;
	}

	static string? TryGetHost(string url) {
		return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
	}
}
