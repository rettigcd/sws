using Saz;
using Auth;

namespace AzureB2c;

/// <summary>
/// Enriches a generically-detected flow with Azure B2C-specific detail. Purely additive:
/// never changes flow correlation or FlowType, only adds IsAzureB2c/B2cDetails.
/// </summary>
internal static class B2cEnricher {

	public static (bool IsAzureB2c, B2cFlowDetails? Details) Enrich(FlowInProgress flow, IReadOnlyList<Exchange> allExchanges) {
		var flowExchanges = allExchanges.Where(s => flow.RelatedExchangeIds.Contains(s.ExchangeId)).ToList();
		var b2cExchange = flowExchanges.FirstOrDefault(s => B2cDetector.IsB2cHost(s.Request.Url));
		if (b2cExchange is null)
			return (false, null);

		(string? tenant, string? policy, string? authorityBaseUrl) = B2cDetector.Extract(b2cExchange.Request);

		var b2cCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var exchange in flowExchanges)
			foreach (var cookie in exchange.Request.Cookies)
				if (B2cCookieNames.IsB2cCookie(cookie.Key))
					b2cCookies[cookie.Key] = cookie.Value;

		var authRequestExchange = flowExchanges.FirstOrDefault(s => s.ExchangeId == flow.AuthorizationRequestExchangeId);
		string? codeChallenge = TryGetQuery(authRequestExchange, "code_challenge");
		string? codeChallengeMethod = TryGetQuery(authRequestExchange, "code_challenge_method");
		string? responseMode = TryGetQuery(authRequestExchange, "response_mode");
		string? responseType = TryGetQuery(authRequestExchange, "response_type");

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

	static string? TryGetQuery(Exchange? exchange, string key) {
		return exchange is not null && exchange.Request.QueryParameters.TryGetValue(key, out string? value) ? value : null;
	}
}
