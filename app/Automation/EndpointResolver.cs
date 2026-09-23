namespace Automation;

using Saz;

internal sealed record ResolvedEndpoints(
	string? AuthorizationEndpoint,
	string? TokenEndpoint,
	string? Issuer,
	string Source
);

/// <summary>
/// Recovers the literal authorize/token endpoint URLs for a detected flow, since
/// DetectedAuthenticationFlow only carries exchange IDs and Discovery may be null.
/// </summary>
internal static class EndpointResolver {

	public static ResolvedEndpoints Resolve(Auth.DetectedAuthenticationFlow flow, IReadOnlyList<Exchange> exchanges) {
		if (flow.Discovery is { AuthorizationEndpoint.Length: > 0 } or { TokenEndpoint.Length: > 0 }) {
			return new ResolvedEndpoints(
				flow.Discovery.AuthorizationEndpoint,
				flow.Discovery.TokenEndpoint,
				flow.Discovery.Issuer,
				"discovery"
			);
		}

		if (flow.B2cDetails is { AuthorizationEndpoint.Length: > 0 } or { TokenEndpoint.Length: > 0 }) {
			return new ResolvedEndpoints(
				flow.B2cDetails.AuthorizationEndpoint,
				flow.B2cDetails.TokenEndpoint,
				flow.Issuer,
				"b2c-details"
			);
		}

		string? authorizationEndpoint = FindCapturedEndpoint(exchanges, flow.AuthorizationRequestExchangeId);
		string? tokenEndpoint = FindCapturedEndpoint(exchanges, flow.TokenRequestExchangeId);
		if (authorizationEndpoint is not null || tokenEndpoint is not null) {
			int? sourceId = flow.AuthorizationRequestExchangeId ?? flow.TokenRequestExchangeId;
			return new ResolvedEndpoints(authorizationEndpoint, tokenEndpoint, flow.Issuer, $"captured-exchange:{sourceId}");
		}

		return new ResolvedEndpoints(null, null, flow.Issuer, "unresolved");
	}

	static string? FindCapturedEndpoint(IReadOnlyList<Exchange> exchanges, int? exchangeId) {
		if (exchangeId is null)
			return null;

		var exchange = exchanges.FirstOrDefault(s => s.ExchangeId == exchangeId);
		if (exchange is null)
			return null;

		return Uri.TryCreate(exchange.Request.Url, UriKind.Absolute, out var uri)
			? uri.GetLeftPart(UriPartial.Path)
			: exchange.Request.Url;
	}
}
