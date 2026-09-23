namespace Auth;

/// <summary>
/// Classifies individual sessions as OIDC/OAuth2-related request/response types.
/// Generic across providers; Azure B2C specifics are layered on separately by AzureB2c.B2cEnricher.
/// </summary>
internal static class SessionClassifier {

	/// <summary>
	/// Classifies only requests/responses that are currently Unknown and preserves existing classifications.
	/// </summary>
	public static IReadOnlyList<Session> ClassifyUnknownSessions(IReadOnlyList<Session> sessions) {
		if (sessions.Count == 0)
			return sessions;

		var classifiedSessions = new List<Session>(sessions.Count);
		var priorSessions = new List<Session>(sessions.Count);

		foreach (var session in sessions) {
			var processedSession = session;

			if (session.Request.RequestType == RequestType.Unknown) {
				var classifiedRequestType = ClassifyRequest(session, priorSessions);
				if (classifiedRequestType != RequestType.Unknown) {
					processedSession = processedSession with { Request = processedSession.Request with { RequestType = classifiedRequestType } };
				}
			}

			if (processedSession.Response.ResponseClassification == ResponseType.Unknown) {
				var classifiedResponseType = ClassifyResponse(processedSession, priorSessions);
				if (classifiedResponseType != ResponseType.Unknown) {
					processedSession = processedSession with { Response = processedSession.Response with { ResponseClassification = classifiedResponseType } };
				}
			}

			classifiedSessions.Add(processedSession);
			priorSessions.Add(processedSession);
		}

		return classifiedSessions;
	}

	/// <summary>
	/// Classifies a session into a high-level OIDC/OAuth2 request type, without reclassifying
	/// a session that already has a known type.
	/// </summary>
	public static RequestType ClassifySession(Session session, IReadOnlyList<Session>? priorSessions = null) {
		if (session.Request.RequestType != RequestType.Unknown)
			return session.Request.RequestType;

		return ClassifyRequest(session, priorSessions ?? []);
	}

	static bool IsDeviceAuthorizationRequest(Session session, OidcDiscoveryDocument? discovery) {
		// Path/discovery-based only: the initial device authorization request has no grant_type,
		// so matching on a "device_code"-ish grant_type would also catch device-code token polls
		// (grant_type=urn:ietf:params:oauth:grant-type:device_code) at the token endpoint.
		return EndpointClassifier.IsDeviceAuthorizationEndpoint(session.Request.Url, discovery);
	}

	static bool IsAuthorizationCallbackRequest(Session session, IReadOnlyList<Session> priorSessions, OidcDiscoveryDocument? discovery) {
		if (EndpointClassifier.IsAuthorizeRequest(session.Request.Url, discovery) || EndpointClassifier.IsTokenRequest(session.Request.Url, discovery))
			return false;

		bool hasCode = session.Request.QueryParameters.ContainsKey("code");
		bool hasState = session.Request.QueryParameters.ContainsKey("state")
			|| session.Request.QueryParameters.ContainsKey("session_state");

		if (hasCode && hasState)
			return true;

		if (OAuthParameterHelpers.TryParseFragmentParameters(session.Request.Fragment, out var callbackFragmentParameters)
			&& OAuthParameterHelpers.HasCodeAndState(callbackFragmentParameters)) {
			return true;
		}

		foreach (var priorSession in priorSessions.Reverse()) {
			if (!OAuthParameterHelpers.IsOauth2AuthorizationRequest(priorSession.Request))
				continue;

			if (!OAuthParameterHelpers.TryGetRequestParameter(priorSession.Request, "redirect_uri", out string redirectUri))
				continue;

			if (!OAuthParameterHelpers.UrlsMatchIgnoringFragment(redirectUri, session.Request.Url))
				continue;

			if (!OAuthParameterHelpers.TryGetRequestParameter(priorSession.Request, "response_mode", out string responseMode)
				|| !responseMode.Equals("fragment", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}

			if (OAuthParameterHelpers.TryParseFragmentFromLocation(priorSession.Response, session.Request.Url, out var locationFragmentParameters)
				&& OAuthParameterHelpers.HasCodeAndState(locationFragmentParameters)) {
				return true;
			}
		}

		return false;
	}

	static bool IsTokenRequestWithGrantType(Session session, OidcDiscoveryDocument? discovery, string grantType) {
		if (!EndpointClassifier.IsTokenRequest(session.Request.Url, discovery))
			return false;

		if (!OAuthParameterHelpers.TryGetRequestParameter(session.Request, "grant_type", out string actualGrantType))
			return false;

		return actualGrantType.Equals(grantType, StringComparison.OrdinalIgnoreCase);
	}

	static RequestType ClassifyRequest(Session session, IReadOnlyList<Session> priorSessions) {
		var discovery = EndpointClassifier.FindRelevantDiscovery(session, priorSessions);

		if (EndpointClassifier.IsOpenIdConfiguration(session.Request.Url))
			return RequestType.Configuration;

		if (IsDeviceAuthorizationRequest(session, discovery))
			return RequestType.AuthorizationRequest_DeviceAuthorization;

		if (EndpointClassifier.IsEndSessionEndpoint(session.Request.Url, discovery))
			return RequestType.EndSessionRequest;

		if (IsTokenRequestWithGrantType(session, discovery, "refresh_token"))
			return RequestType.RefreshTokenRequest;

		if (IsTokenRequestWithGrantType(session, discovery, "authorization_code")
			&& OAuthParameterHelpers.TryGetRequestParameter(session.Request, "code", out _))
			return RequestType.AuthorizationCodeTokenRequest;

		if (IsTokenRequestWithGrantType(session, discovery, "client_credentials"))
			return RequestType.ClientCredentialsTokenRequest;

		if (IsTokenRequestWithGrantType(session, discovery, "password"))
			return RequestType.PasswordTokenRequest;

		if (IsTokenRequestWithGrantType(session, discovery, "urn:ietf:params:oauth:grant-type:device_code"))
			return RequestType.DeviceCodeTokenRequest;

		if (IsAuthorizationCallbackRequest(session, priorSessions, discovery))
			return RequestType.AuthorizationCallbackRequest;

		if (OAuthParameterHelpers.IsOauth2AuthorizationRequest(session.Request)) {
			if (!session.Request.QueryParameters.TryGetValue("response_type", out string? responseType)
				|| string.IsNullOrWhiteSpace(responseType))
				return RequestType.AuthorizationRequest_Unknown;

			bool hasCode = OAuthParameterHelpers.HasResponseType(responseType, "code");
			bool hasToken = OAuthParameterHelpers.HasResponseType(responseType, "token");
			bool hasIdToken = OAuthParameterHelpers.HasResponseType(responseType, "id_token");
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

	static ResponseType ClassifyResponse(Session session, IReadOnlyList<Session> priorSessions) {
		var response = session.Response;
		var request = session.Request;
		var discovery = EndpointClassifier.FindRelevantDiscovery(session, priorSessions);

		if (response.StatusCode >= 400)
			return ResponseType.ErrorResponse;

		if (EndpointClassifier.IsTokenRequest(request.Url, discovery) && response.StatusCode == 200) {
			if (response.ResponseJson?.TryGetProperty("access_token", out _) == true
				|| response.ResponseJson?.TryGetProperty("id_token", out _) == true
				|| response.ResponseJson?.TryGetProperty("refresh_token", out _) == true)
				return ResponseType.TokenResponse;
		}

		if (EndpointClassifier.IsDeviceAuthorizationEndpoint(request.Url, discovery) && response.StatusCode == 200) {
			if (response.ResponseJson?.TryGetProperty("device_code", out _) == true
				|| response.ResponseJson?.TryGetProperty("user_code", out _) == true)
				return ResponseType.DeviceCodeResponse;
		}

		if (EndpointClassifier.IsOpenIdConfiguration(request.Url) && response.StatusCode == 200) {
			if (response.ResponseJson?.TryGetProperty("authorization_endpoint", out _) == true
				|| response.ResponseJson?.TryGetProperty("issuer", out _) == true)
				return ResponseType.ConfigurationResponse;
		}

		if (response.StatusCode is >= 300 and < 400) {
			if (response.Headers.TryGetValue("Location", out string? location)
				&& (location.Contains("code=", StringComparison.OrdinalIgnoreCase)
					|| location.Contains("error=", StringComparison.OrdinalIgnoreCase)
					|| location.Contains("#code=", StringComparison.OrdinalIgnoreCase)
					|| location.Contains("#error=", StringComparison.OrdinalIgnoreCase)))
				return ResponseType.AuthorizationRedirect;
		}

		if (response.StatusCode is >= 200 and < 300)
			return ResponseType.SuccessResponse;

		return ResponseType.Unknown;
	}
}
