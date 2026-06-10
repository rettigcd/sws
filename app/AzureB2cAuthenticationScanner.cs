using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Detects OAuth/B2C-related request types from captured sessions and returns them in request order.
/// </summary>
internal static class AzureB2cAuthenticationScanner {
	/// <summary>
	/// Scans sessions and emits OAuth/B2C-related request classifications in original session order.
	/// </summary>
	public static AzureB2cAuthenticationReport Scan(IReadOnlyList<Session> sessions) {
		var classifiedSessions = ClassifyUnknownSessions(sessions);
		var requests = new List<AzureB2cRequestClassification>();

		foreach (var session in classifiedSessions) {
			var requestType = session.Request.RequestType;

			if (requestType == RequestType.Unknown)
				continue;

			requests.Add(new AzureB2cRequestClassification(session.SessionId, requestType));
		}

		return new AzureB2cAuthenticationReport(DateTimeOffset.UtcNow, requests);
	}

	/// <summary>
	/// Classifies only requests that are currently Unknown and preserves existing classifications.
	/// </summary>
	internal static IReadOnlyList<Session> ClassifyUnknownSessions(IReadOnlyList<Session> sessions) {
		if (sessions.Count == 0)
			return sessions;

		var classifiedSessions = new List<Session>(sessions.Count);
		var priorSessions = new List<Session>(sessions.Count);

		foreach (var session in sessions) {
			if (session.Request.RequestType != RequestType.Unknown) {
				classifiedSessions.Add(session);
				priorSessions.Add(session);
				continue;
			}

			var classifiedRequestType = Classify(session, priorSessions);
			var classifiedSession = classifiedRequestType == RequestType.Unknown
				? session
				: session with { Request = session.Request with { RequestType = classifiedRequestType } };

			classifiedSessions.Add(classifiedSession);
			priorSessions.Add(classifiedSession);
		}

		return classifiedSessions;
	}

	/// <summary>
	/// Returns whether the URL targets the OAuth2 authorize endpoint.
	/// </summary>
	static bool IsAuthorizeRequest(string url) {
		return ContainsPath(url, "/oauth2/v2.0/authorize");
	}

	/// <summary>
	/// Returns whether the URL targets the OAuth2 token endpoint.
	/// </summary>
	static bool IsTokenRequest(string url) {
		return ContainsPath(url, "/oauth2/v2.0/token");
	}

	/// <summary>
	/// Returns whether the URL targets OpenID Connect discovery metadata.
	/// </summary>
	static bool IsOpenIdConfiguration(string url) {
		return ContainsPath(url, "/.well-known/openid-configuration");
	}

	/// <summary>
	/// Checks whether a URL path contains the given marker, supporting non-absolute URLs.
	/// </summary>
	static bool ContainsPath(string url, string marker) {
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			return url.Contains(marker, StringComparison.OrdinalIgnoreCase);

		return uri.AbsolutePath.Contains(marker, StringComparison.OrdinalIgnoreCase);
	}

	static bool IsOauth2_AuthorizationRequest(Request request){
		return request.QueryParameters.ContainsKey("response_type")
			&& request.QueryParameters.ContainsKey("client_id");
	}

	static bool IsDeviceAuthorizationRequest(Session session) {
		if (ContainsPath(session.Request.Url, "/oauth2/v2.0/devicecode"))
			return true;

		if (session.Request.QueryParameters.TryGetValue("grant_type", out var queryGrantType)
			&& queryGrantType.Contains("device_code", StringComparison.OrdinalIgnoreCase))
			return true;

		if (session.Request.FormBody is not { Count: > 0 })
			return false;

		var grantType = session.Request.FormBody
			.FirstOrDefault(entry => entry.Key.Equals("grant_type", StringComparison.OrdinalIgnoreCase))
			?.Value;

		return grantType?.Contains("device_code", StringComparison.OrdinalIgnoreCase) == true;
	}

	static bool HasResponseType(string responseTypesRaw, string expected) {
		return responseTypesRaw
			.Split([' ', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Any(value => value.Equals(expected, StringComparison.OrdinalIgnoreCase));
	}

	static bool TryGetRequestParameter(Request request, string key, out string value) {
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

	static bool IsAuthorizationCallbackRequest(Session session) {
		return IsAuthorizationCallbackRequest(session, []);
	}

	static bool IsAuthorizationCallbackRequest(Session session, IReadOnlyList<Session> priorSessions) {
		if (IsAuthorizeRequest(session.Request.Url) || IsTokenRequest(session.Request.Url))
			return false;

		var hasCode = session.Request.QueryParameters.ContainsKey("code");
		var hasState = session.Request.QueryParameters.ContainsKey("state")
			|| session.Request.QueryParameters.ContainsKey("session_state");

		if (hasCode && hasState)
			return true;

		if (TryParseFragmentParameters(session.Request.Fragment, out var callbackFragmentParameters)
			&& HasCodeAndState(callbackFragmentParameters)) {
			return true;
		}

		foreach (var priorSession in priorSessions.Reverse()) {
			if (!IsOauth2_AuthorizationRequest(priorSession.Request))
				continue;

			if (!TryGetRequestParameter(priorSession.Request, "redirect_uri", out var redirectUri))
				continue;

			if (!UrlsMatchIgnoringFragment(redirectUri, session.Request.Url))
				continue;

			if (!TryGetRequestParameter(priorSession.Request, "response_mode", out var responseMode)
				|| !responseMode.Equals("fragment", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}

			if (TryParseFragmentFromLocation(priorSession.Response, session.Request.Url, out var locationFragmentParameters)
				&& HasCodeAndState(locationFragmentParameters)) {
				return true;
			}
		}

		return false;
	}

	static bool IsAuthorizationCodeTokenRequest(Session session) {
		if (!IsTokenRequest(session.Request.Url))
			return false;

		if (!TryGetRequestParameter(session.Request, "grant_type", out var grantType))
			return false;

		if (!grantType.Equals("authorization_code", StringComparison.OrdinalIgnoreCase))
			return false;

		return TryGetRequestParameter(session.Request, "code", out _);
	}

	static bool IsRefreshTokenRequest(Session session) {
		if (!IsTokenRequest(session.Request.Url))
			return false;

		if (!TryGetRequestParameter(session.Request, "grant_type", out var grantType))
			return false;

		if (!grantType.Equals("refresh_token", StringComparison.OrdinalIgnoreCase))
			return false;

		return TryGetRequestParameter(session.Request, "refresh_token", out _);
	}

	static bool TryParseFragmentParameters(string? fragment, out Dictionary<string, string> parameters) {
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

	static bool HasCodeAndState(IReadOnlyDictionary<string, string> parameters) {
		return parameters.ContainsKey("code")
			&& (parameters.ContainsKey("state") || parameters.ContainsKey("session_state"));
	}

	static bool UrlsMatchIgnoringFragment(string left, string right) {
		if (Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
			&& Uri.TryCreate(right, UriKind.Absolute, out var rightUri)) {
			return leftUri.GetLeftPart(UriPartial.Path).Equals(rightUri.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase)
				&& string.Equals(leftUri.Query, rightUri.Query, StringComparison.OrdinalIgnoreCase);
		}

		var normalizedLeft = left.Split('#', 2)[0];
		var normalizedRight = right.Split('#', 2)[0];
		return normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
	}

	static bool TryParseFragmentFromLocation(Response response, string callbackUrl, out Dictionary<string, string> parameters) {
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

	static RequestType Classify(Session session, IReadOnlyList<Session> priorSessions){
		if (IsOpenIdConfiguration(session.Request.Url))
			return RequestType.EndpointDict;

		if (IsDeviceAuthorizationRequest(session))
			return RequestType.AuthorizationRequest_DeviceAuthorization;

		if (IsRefreshTokenRequest(session))
			return RequestType.RefreshTokenRequest;

		if (IsAuthorizationCodeTokenRequest(session))
			return RequestType.AuthorizationCodeTokenRequest;

		if (IsAuthorizationCallbackRequest(session, priorSessions))
			return RequestType.AuthorizationCallbackRequest;

		// ---- Authorization Request ---- 
		if (IsOauth2_AuthorizationRequest(session.Request)) {

			if (!session.Request.QueryParameters.TryGetValue("response_type", out var responseType)
				|| string.IsNullOrWhiteSpace(responseType))
				return RequestType.AuthorizationRequest_Unknown;

			bool hasCode = HasResponseType(responseType, "code");
			bool hasToken = HasResponseType(responseType, "token");
			bool hasIdToken = HasResponseType(responseType, "id_token");
			bool hasPkce = session.Request.QueryParameters.ContainsKey("code_challenge");

			if (hasCode && (hasToken || hasIdToken))
				return RequestType.AuthorizationRequest_Hybrid;

			if (!hasCode && (hasToken || hasIdToken))
				return RequestType.AuthorizationRequest_Implicit;

			if (hasCode && hasPkce)
				return RequestType.AuthorizationRequest_AuthCodeWithPKCE;

			if (hasCode)
				return RequestType.AuthorizationRequest_AuthCode;

			return RequestType.AuthorizationRequest_Unknown;
		}

		return RequestType.Unknown;
	}

	/// <summary>
	/// Classifies a session into a high-level Azure B2C/OAuth session type.
	/// </summary>
	internal static RequestType ClassifySession(Session session, IReadOnlyList<Session>? priorSessions) {
		if (session.Request.RequestType != RequestType.Unknown)
			return session.Request.RequestType;

		return Classify(session, priorSessions ?? []);
	}

	/// <summary>
	/// Classifies a session into a high-level Azure B2C/OAuth session type.
	/// </summary>
	internal static RequestType ClassifySession(Session session) {
		if (session.Request.RequestType != RequestType.Unknown)
			return session.Request.RequestType;

		return Classify(session, []);
	}

}

internal sealed record AzureB2cAuthenticationReport(
	DateTimeOffset GeneratedUtc,
	List<AzureB2cRequestClassification> Requests
);

internal sealed record AzureB2cRequestClassification(
	int SessionId,
	RequestType RequestType
);

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum RequestType {
	Unknown,

	// .wellknown that says where the endpoints are.
	EndpointDict,

	// Basic Authorization Request
	AuthorizationRequest_Unknown,

	// AuthCode + PKCE
	AuthorizationRequest_AuthCodeWithPKCE,

	// AuthCode
	AuthorizationRequest_AuthCode,

	// AuthCode
	AuthorizationRequest_Implicit,

	// AuthCode
	AuthorizationRequest_Hybrid,

	// For Devices
	AuthorizationRequest_DeviceAuthorization,

	// the result of the AuthCodeRedirect back to the client app
	AuthorizationCallbackRequest, 
		// → TokenExchangeInitiated
		// → SpaShellResponse or CallbackPageResponse

	// Client POSTS: 
	// => grant_type=authorization_code  ->  I have an authorization code and want tokens."
	// => code
	// => code_verifier, 				 ->  This is Authorization Code Flow with PKCE.
	AuthorizationCodeTokenRequest,

	// Request the refresh token.
	RefreshTokenRequest

}