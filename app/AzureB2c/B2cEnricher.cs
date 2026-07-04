using Auth;

namespace AzureB2c;

/// <summary>
/// Enriches a generically-detected flow with Azure B2C-specific detail. Purely additive:
/// never changes flow correlation or FlowType, only adds IsAzureB2c/B2cDetails.
/// </summary>
internal static class B2cEnricher {

	public static (bool IsAzureB2c, B2cFlowDetails? Details) Enrich(FlowInProgress flow, IReadOnlyList<Session> allSessions) {
		var flowSessions = allSessions.Where(s => flow.RelatedSessionIds.Contains(s.SessionId)).ToList();
		var b2cSession = flowSessions.FirstOrDefault(s => B2cDetector.IsB2cHost(s.Request.Url));
		if (b2cSession is null)
			return (false, null);

		(string? tenant, string? policy, string? authorityBaseUrl) = B2cDetector.Extract(b2cSession.Request);

		var b2cCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var session in flowSessions)
			foreach (var cookie in session.Request.Cookies)
				if (B2cCookieNames.IsB2cCookie(cookie.Key))
					b2cCookies[cookie.Key] = cookie.Value;

		var authRequestSession = flowSessions.FirstOrDefault(s => s.SessionId == flow.AuthorizationRequestSessionId);
		string? codeChallenge = TryGetQuery(authRequestSession, "code_challenge");
		string? codeChallengeMethod = TryGetQuery(authRequestSession, "code_challenge_method");
		string? responseMode = TryGetQuery(authRequestSession, "response_mode");
		string? responseType = TryGetQuery(authRequestSession, "response_type");

		var details = new B2cFlowDetails(
			tenant,
			policy,
			authorityBaseUrl,
			flow.Discovery?.AuthorizationEndpoint,
			flow.Discovery?.TokenEndpoint,
			flow.RedirectUri,
			flow.ClientId,
			responseMode,
			responseType,
			flow.Scopes,
			codeChallenge,
			codeChallengeMethod,
			b2cCookies
		);

		return (true, details);
	}

	static string? TryGetQuery(Session? session, string key) {
		return session is not null && session.Request.QueryParameters.TryGetValue(key, out string? value) ? value : null;
	}
}
