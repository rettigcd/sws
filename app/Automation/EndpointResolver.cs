namespace Automation;

internal sealed record ResolvedEndpoints(
	string? AuthorizationEndpoint,
	string? TokenEndpoint,
	string? Issuer,
	string Source
);

/// <summary>
/// Recovers the literal authorize/token endpoint URLs for a detected flow, since
/// DetectedAuthenticationFlow only carries session IDs and Discovery may be null.
/// </summary>
internal static class EndpointResolver {

	public static ResolvedEndpoints Resolve(Auth.DetectedAuthenticationFlow flow, IReadOnlyList<Session> sessions) {
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

		var authorizationEndpoint = FindCapturedEndpoint(sessions, flow.AuthorizationRequestSessionId);
		var tokenEndpoint = FindCapturedEndpoint(sessions, flow.TokenRequestSessionId);
		if (authorizationEndpoint is not null || tokenEndpoint is not null) {
			var sourceId = flow.AuthorizationRequestSessionId ?? flow.TokenRequestSessionId;
			return new ResolvedEndpoints(authorizationEndpoint, tokenEndpoint, flow.Issuer, $"captured-session:{sourceId}");
		}

		return new ResolvedEndpoints(null, null, flow.Issuer, "unresolved");
	}

	static string? FindCapturedEndpoint(IReadOnlyList<Session> sessions, int? sessionId) {
		if (sessionId is null)
			return null;

		var session = sessions.FirstOrDefault(s => s.SessionId == sessionId);
		if (session is null)
			return null;

		return Uri.TryCreate(session.Request.Url, UriKind.Absolute, out var uri)
			? uri.GetLeftPart(UriPartial.Path)
			: session.Request.Url;
	}
}
