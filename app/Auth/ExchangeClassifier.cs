namespace Auth;

using Saz;

/// <summary>
/// Classifies individual exchanges as OIDC/OAuth2-related request/response types.
/// Generic across providers; Azure B2C specifics are layered on separately by AzureB2c.B2cEnricher.
/// </summary>
internal static class ExchangeClassifier {

	/// <summary>
	/// Classifies every exchange's request and response. Each exchange is classified with
	/// the exchanges before it as context (e.g. to find the relevant discovery document).
	/// </summary>
	public static IReadOnlyList<ClassifiedExchange> Classify(IReadOnlyList<Exchange> exchanges) {
		var classified = new List<ClassifiedExchange>(exchanges.Count);
		var priorExchanges = new List<Exchange>(exchanges.Count);

		foreach (var exchange in exchanges) {
			classified.Add(new ClassifiedExchange(
				exchange,
				ClassifyRequest(exchange, priorExchanges),
				ClassifyResponse(exchange, priorExchanges)
			));
			priorExchanges.Add(exchange);
		}

		return classified;
	}

	static bool IsDeviceAuthorizationRequest(Exchange exchange, OidcDiscoveryDocument? discovery) {
		// Path/discovery-based only: the initial device authorization request has no grant_type,
		// so matching on a "device_code"-ish grant_type would also catch device-code token polls
		// (grant_type=urn:ietf:params:oauth:grant-type:device_code) at the token endpoint.
		return EndpointClassifier.IsDeviceAuthorizationEndpoint(exchange.Request.Url, discovery);
	}

	static bool IsAuthorizationCallbackRequest(Exchange exchange, IReadOnlyList<Exchange> priorExchanges, OidcDiscoveryDocument? discovery) {
		if (EndpointClassifier.IsAuthorizeRequest(exchange.Request.Url, discovery) || EndpointClassifier.IsTokenRequest(exchange.Request.Url, discovery))
			return false;

		bool hasCode = exchange.Request.QueryParameters.ContainsKey("code");
		bool hasState = exchange.Request.QueryParameters.ContainsKey("state")
			|| exchange.Request.QueryParameters.ContainsKey("session_state");

		if (hasCode && hasState)
			return true;

		if (OAuthParameterHelpers.TryParseFragmentParameters(exchange.Request.Fragment, out var callbackFragmentParameters)
			&& OAuthParameterHelpers.HasCodeAndState(callbackFragmentParameters)) {
			return true;
		}

		foreach (var priorExchange in priorExchanges.Reverse()) {
			if (!OAuthParameterHelpers.IsOauth2AuthorizationRequest(priorExchange.Request))
				continue;

			if (!OAuthParameterHelpers.TryGetRequestParameter(priorExchange.Request, "redirect_uri", out string redirectUri))
				continue;

			if (!OAuthParameterHelpers.UrlsMatchIgnoringFragment(redirectUri, exchange.Request.Url))
				continue;

			if (!OAuthParameterHelpers.TryGetRequestParameter(priorExchange.Request, "response_mode", out string responseMode)
				|| !responseMode.Equals("fragment", StringComparison.OrdinalIgnoreCase)) {
				continue;
			}

			if (OAuthParameterHelpers.TryParseFragmentFromLocation(priorExchange.Response, exchange.Request.Url, out var locationFragmentParameters)
				&& OAuthParameterHelpers.HasCodeAndState(locationFragmentParameters)) {
				return true;
			}
		}

		return false;
	}

	static bool IsTokenRequestWithGrantType(Exchange exchange, OidcDiscoveryDocument? discovery, string grantType) {
		if (!EndpointClassifier.IsTokenRequest(exchange.Request.Url, discovery))
			return false;

		if (!OAuthParameterHelpers.TryGetRequestParameter(exchange.Request, "grant_type", out string actualGrantType))
			return false;

		return actualGrantType.Equals(grantType, StringComparison.OrdinalIgnoreCase);
	}

	public static RequestType ClassifyRequest(Exchange exchange, IReadOnlyList<Exchange> priorExchanges) {
		var discovery = EndpointClassifier.FindRelevantDiscovery(exchange, priorExchanges);

		if (EndpointClassifier.IsOpenIdConfiguration(exchange.Request.Url))
			return RequestType.Configuration;

		if (IsDeviceAuthorizationRequest(exchange, discovery))
			return RequestType.AuthorizationRequest_DeviceAuthorization;

		if (EndpointClassifier.IsEndSessionEndpoint(exchange.Request.Url, discovery))
			return RequestType.EndSessionRequest;

		if (IsTokenRequestWithGrantType(exchange, discovery, "refresh_token"))
			return RequestType.RefreshTokenRequest;

		if (IsTokenRequestWithGrantType(exchange, discovery, "authorization_code")
			&& OAuthParameterHelpers.TryGetRequestParameter(exchange.Request, "code", out _))
			return RequestType.AuthorizationCodeTokenRequest;

		if (IsTokenRequestWithGrantType(exchange, discovery, "client_credentials"))
			return RequestType.ClientCredentialsTokenRequest;

		if (IsTokenRequestWithGrantType(exchange, discovery, "password"))
			return RequestType.PasswordTokenRequest;

		if (IsTokenRequestWithGrantType(exchange, discovery, "urn:ietf:params:oauth:grant-type:device_code"))
			return RequestType.DeviceCodeTokenRequest;

		if (IsAuthorizationCallbackRequest(exchange, priorExchanges, discovery))
			return RequestType.AuthorizationCallbackRequest;

		if (OAuthParameterHelpers.IsOauth2AuthorizationRequest(exchange.Request)) {
			if (!exchange.Request.QueryParameters.TryGetValue("response_type", out string? responseType)
				|| string.IsNullOrWhiteSpace(responseType))
				return RequestType.AuthorizationRequest_Unknown;

			bool hasCode = OAuthParameterHelpers.HasResponseType(responseType, "code");
			bool hasToken = OAuthParameterHelpers.HasResponseType(responseType, "token");
			bool hasIdToken = OAuthParameterHelpers.HasResponseType(responseType, "id_token");
			bool hasPkce = exchange.Request.QueryParameters.ContainsKey("code_challenge");

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

	public static ResponseType ClassifyResponse(Exchange exchange, IReadOnlyList<Exchange> priorExchanges) {
		var response = exchange.Response;
		var request = exchange.Request;
		var discovery = EndpointClassifier.FindRelevantDiscovery(exchange, priorExchanges);

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
