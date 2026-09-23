namespace Auth;

using Saz;

/// <summary>
/// Mutable in-progress flow used while correlating exchanges. Converted to an immutable
/// DetectedAuthenticationFlow once variables/replay-requirements/warnings have been attached.
/// </summary>
internal sealed class FlowInProgress {
	public required string FlowId { get; init; }
	public AuthFlowType FlowType { get; set; } = AuthFlowType.Unknown;
	public double Confidence { get; set; } = 1.0;
	public List<string> ConfidenceReasons { get; } = [];

	public OidcDiscoveryDocument? Discovery { get; set; }
	public int? DiscoveryRequestExchangeId { get; set; }
	public int? AuthorizationRequestExchangeId { get; set; }
	public int? AuthorizationCallbackExchangeId { get; set; }
	public int? TokenRequestExchangeId { get; set; }
	public List<int> RelatedExchangeIds { get; } = [];

	public string? Issuer { get; set; }
	public string? ClientId { get; set; }
	public string? RedirectUri { get; set; }
	public List<string> Scopes { get; set; } = [];

	public string? CapturedState { get; set; }
	public string? CapturedCode { get; set; }
	public string? CapturedDeviceCode { get; set; }
	public string? IssuedRefreshToken { get; set; }

	public List<FlowWarning> Warnings { get; } = [];

	public void AddWarning(FlowWarningKind kind, string message, params int[] relatedExchangeIds) {
		Warnings.Add(new FlowWarning(kind, message, relatedExchangeIds.Length > 0 ? relatedExchangeIds : [.. RelatedExchangeIds]));
	}

	public void ReduceConfidence(double delta, string reason) {
		Confidence = Math.Max(0.1, Confidence - delta);
		ConfidenceReasons.Add(reason);
	}
}

internal static class FlowCorrelator {

	static readonly HashSet<RequestType> AuthorizationRequestTypes = [
		RequestType.AuthorizationRequest_Unknown,
		RequestType.AuthorizationRequest_AuthCodeWithPKCE,
		RequestType.AuthorizationRequest_AuthCode,
		RequestType.AuthorizationRequest_Implicit,
		RequestType.AuthorizationRequest_Hybrid,
	];

	public static List<FlowInProgress> Correlate(IReadOnlyList<ClassifiedExchange> classifiedExchanges) {
		var flows = new List<FlowInProgress>();
		int nextFlowId = 1;
		var exchanges = classifiedExchanges.Select(c => c.Exchange).ToList();
		var requestTypeById = classifiedExchanges.ToDictionary(c => c.ExchangeId, c => c.RequestType);

		var authRequests = OfRequestTypes(classifiedExchanges, AuthorizationRequestTypes);
		var callbacks = OfRequestTypes(classifiedExchanges, RequestType.AuthorizationCallbackRequest);
		var authCodeTokenRequests = OfRequestTypes(classifiedExchanges, RequestType.AuthorizationCodeTokenRequest);
		var refreshTokenRequests = OfRequestTypes(classifiedExchanges, RequestType.RefreshTokenRequest);
		var clientCredentialTokenRequests = OfRequestTypes(classifiedExchanges, RequestType.ClientCredentialsTokenRequest);
		var passwordTokenRequests = OfRequestTypes(classifiedExchanges, RequestType.PasswordTokenRequest);
		var deviceCodeTokenRequests = OfRequestTypes(classifiedExchanges, RequestType.DeviceCodeTokenRequest);
		var deviceAuthRequests = OfRequestTypes(classifiedExchanges, RequestType.AuthorizationRequest_DeviceAuthorization);

		// Step 3: seed one flow per authorization request.
		var flowByAuthRequestId = new Dictionary<int, FlowInProgress>();
		foreach (var authRequest in authRequests) {
			var flow = SeedFlowFromAuthorizationRequest(authRequest, requestTypeById[authRequest.ExchangeId], nextFlowId++);
			flowByAuthRequestId[authRequest.ExchangeId] = flow;
			flows.Add(flow);
		}

		// Step 4: match callbacks -> authorization requests.
		var usedAuthRequestIds = new HashSet<int>();
		foreach (var callback in callbacks) {
			var match = MatchCallbackToAuthRequest(callback, authRequests, usedAuthRequestIds);
			if (match is null) {
				var orphan = SeedOrphanFlow(callback, nextFlowId++, "Authorization callback with no matching authorization request in capture.");
				orphan.AuthorizationCallbackExchangeId = callback.ExchangeId;
				flows.Add(orphan);
				continue;
			}

			(Exchange authRequest, string reason, double confidenceDelta) = match.Value;
			usedAuthRequestIds.Add(authRequest.ExchangeId);
			var flow = flowByAuthRequestId[authRequest.ExchangeId];
			AttachCallback(flow, callback, reason, confidenceDelta);
		}

		foreach (var authRequest in authRequests) {
			var flow = flowByAuthRequestId[authRequest.ExchangeId];
			if (flow.AuthorizationCallbackExchangeId is null)
				flow.AddWarning(FlowWarningKind.MissingCallback, "No authorization callback observed for this authorization request.", authRequest.ExchangeId);
		}

		// Step 5: match token requests -> flows.
		var tokenMatchedFlows = new HashSet<string>();
		foreach (var tokenRequest in authCodeTokenRequests) {
			var flow = MatchAuthorizationCodeTokenRequest(tokenRequest, flows);
			if (flow is null) {
				flow = SeedOrphanFlow(tokenRequest, nextFlowId++, "Authorization-code token exchange with no matching authorization/callback in capture.");
				flow.FlowType = AuthFlowType.AuthorizationCode;
				flows.Add(flow);
			}
			AttachTokenRequest(flow, tokenRequest);
			CaptureIssuedRefreshToken(flow, tokenRequest);
			tokenMatchedFlows.Add(flow.FlowId);
		}

		foreach (var flow in flows) {
			if (flow.AuthorizationCallbackExchangeId is not null && flow.TokenRequestExchangeId is null
				&& flow.FlowType is AuthFlowType.AuthorizationCode or AuthFlowType.AuthorizationCodeWithPkce) {
				flow.AddWarning(FlowWarningKind.MissingTokenExchange, "Authorization callback observed but no token exchange followed.");
			}
		}

		foreach (var tokenRequest in refreshTokenRequests) {
			var flow = MatchRefreshTokenRequest(tokenRequest, flows);
			if (flow is not null) {
				flow.RelatedExchangeIds.Add(tokenRequest.ExchangeId);
				continue;
			}

			var orphan = SeedOrphanFlow(tokenRequest, nextFlowId++, "Refresh-token request with no originating flow found in capture.");
			orphan.FlowType = AuthFlowType.RefreshToken;
			orphan.TokenRequestExchangeId = tokenRequest.ExchangeId;
			flows.Add(orphan);
		}

		foreach (var tokenRequest in clientCredentialTokenRequests) {
			var flow = SeedStandaloneFlow(tokenRequest, nextFlowId++);
			flow.FlowType = AuthFlowType.ClientCredentials;
			flow.TokenRequestExchangeId = tokenRequest.ExchangeId;
			flow.ClientId = TryGet(tokenRequest.Request, "client_id");
			flow.Scopes = SplitScopes(TryGet(tokenRequest.Request, "scope"));
			flows.Add(flow);
		}

		foreach (var tokenRequest in passwordTokenRequests) {
			var flow = SeedStandaloneFlow(tokenRequest, nextFlowId++);
			flow.FlowType = AuthFlowType.ResourceOwnerPasswordCredentials;
			flow.TokenRequestExchangeId = tokenRequest.ExchangeId;
			flow.ClientId = TryGet(tokenRequest.Request, "client_id");
			flow.Scopes = SplitScopes(TryGet(tokenRequest.Request, "scope"));
			flows.Add(flow);
		}

		CorrelateDeviceCodeFlows(deviceAuthRequests, deviceCodeTokenRequests, flows, ref nextFlowId);

		// Step 6: attach discovery documents.
		foreach (var flow in flows)
			AttachDiscovery(flow, exchanges);

		return flows;
	}

	static List<Exchange> OfRequestTypes(IReadOnlyList<ClassifiedExchange> classifiedExchanges, RequestType requestType) {
		return OfRequestTypes(classifiedExchanges, [requestType]);
	}

	static List<Exchange> OfRequestTypes(IReadOnlyList<ClassifiedExchange> classifiedExchanges, HashSet<RequestType> requestTypes) {
		return classifiedExchanges.Where(c => requestTypes.Contains(c.RequestType)).Select(c => c.Exchange).ToList();
	}

	static FlowInProgress SeedFlowFromAuthorizationRequest(Exchange authRequest, RequestType requestType, int flowId) {
		var flow = new FlowInProgress { FlowId = $"flow-{flowId}" };
		flow.AuthorizationRequestExchangeId = authRequest.ExchangeId;
		flow.RelatedExchangeIds.Add(authRequest.ExchangeId);
		flow.ClientId = TryGet(authRequest.Request, "client_id");
		flow.RedirectUri = TryGet(authRequest.Request, "redirect_uri");
		flow.Scopes = SplitScopes(TryGet(authRequest.Request, "scope"));
		flow.CapturedState = TryGet(authRequest.Request, "state");
		flow.FlowType = requestType switch {
			RequestType.AuthorizationRequest_AuthCodeWithPKCE => AuthFlowType.AuthorizationCodeWithPkce,
			RequestType.AuthorizationRequest_AuthCode => AuthFlowType.AuthorizationCode,
			RequestType.AuthorizationRequest_Implicit => AuthFlowType.Implicit,
			RequestType.AuthorizationRequest_Hybrid => AuthFlowType.Hybrid,
			_ => AuthFlowType.Unknown,
		};

		return flow;
	}

	static FlowInProgress SeedOrphanFlow(Exchange exchange, int flowId, string warningMessage) {
		var flow = new FlowInProgress { FlowId = $"flow-{flowId}" };
		flow.RelatedExchangeIds.Add(exchange.ExchangeId);
		flow.Confidence = 0.3;
		flow.ConfidenceReasons.Add("Single unmatched exchange; no correlation performed.");
		flow.AddWarning(FlowWarningKind.IncompleteFlow, warningMessage, exchange.ExchangeId);
		return flow;
	}

	static FlowInProgress SeedStandaloneFlow(Exchange exchange, int flowId) {
		var flow = new FlowInProgress { FlowId = $"flow-{flowId}" };
		flow.RelatedExchangeIds.Add(exchange.ExchangeId);
		flow.ConfidenceReasons.Add("Self-contained grant type; no cross-exchange correlation required.");
		return flow;
	}

	static (Exchange AuthRequest, string Reason, double ConfidenceDelta)? MatchCallbackToAuthRequest(
		Exchange callback,
		List<Exchange> authRequests,
		HashSet<int> usedAuthRequestIds
	) {
		var priorCandidates = authRequests
			.Where(a => a.ExchangeId < callback.ExchangeId && !usedAuthRequestIds.Contains(a.ExchangeId))
			.OrderByDescending(a => a.ExchangeId)
			.ToList();

		if (priorCandidates.Count == 0)
			return null;

		string? callbackState = callback.Request.QueryParameters.TryGetValue("state", out string? qsState) ? qsState : null;
		if (callbackState is null
			&& OAuthParameterHelpers.TryParseFragmentParameters(callback.Request.Fragment, out var fragmentParams)
			&& fragmentParams.TryGetValue("state", out string? fragState)) {
			callbackState = fragState;
		}

		if (callbackState is not null) {
			var stateMatch = priorCandidates.FirstOrDefault(a => TryGet(a.Request, "state") == callbackState);
			if (stateMatch is not null)
				return (stateMatch, "state match", 0.0);
		}

		foreach (var candidate in priorCandidates) {
			string? redirectUri = TryGet(candidate.Request, "redirect_uri");
			if (redirectUri is not null && OAuthParameterHelpers.UrlsMatchIgnoringFragment(redirectUri, callback.Request.Url))
				return (candidate, "redirect_uri + sequence match", 0.3);
		}

		string? host = Uri.TryCreate(callback.Request.Url, UriKind.Absolute, out var callbackUri) ? callbackUri.Host : null;
		var sequenceMatch = priorCandidates.FirstOrDefault(a =>
			host is null || (Uri.TryCreate(a.Request.Url, UriKind.Absolute, out var authUri) && authUri.Host == host)
		) ?? priorCandidates[0];

		return (sequenceMatch, "sequence-only fallback", 0.6);
	}

	static void AttachCallback(FlowInProgress flow, Exchange callback, string reason, double confidenceDelta) {
		flow.AuthorizationCallbackExchangeId = callback.ExchangeId;
		flow.RelatedExchangeIds.Add(callback.ExchangeId);
		flow.ReduceConfidence(confidenceDelta, reason);

		string? code = callback.Request.QueryParameters.TryGetValue("code", out string? qsCode) ? qsCode : null;
		if (code is null
			&& OAuthParameterHelpers.TryParseFragmentParameters(callback.Request.Fragment, out var fragmentParams)
			&& fragmentParams.TryGetValue("code", out string? fragCode)) {
			code = fragCode;
		}

		flow.CapturedCode = code;
	}

	static FlowInProgress? MatchAuthorizationCodeTokenRequest(Exchange tokenRequest, List<FlowInProgress> flows) {
		string? code = TryGet(tokenRequest.Request, "code");
		if (code is not null) {
			var exact = flows.FirstOrDefault(f => f.CapturedCode == code && f.TokenRequestExchangeId is null);
			if (exact is not null)
				return exact;
		}

		return flows
			.Where(f => f.TokenRequestExchangeId is null
				&& f.AuthorizationCallbackExchangeId is not null
				&& f.AuthorizationCallbackExchangeId < tokenRequest.ExchangeId
				&& f.FlowType is AuthFlowType.AuthorizationCode or AuthFlowType.AuthorizationCodeWithPkce)
			.OrderByDescending(f => f.AuthorizationCallbackExchangeId)
			.FirstOrDefault();
	}

	static void AttachTokenRequest(FlowInProgress flow, Exchange tokenRequest) {
		flow.TokenRequestExchangeId = tokenRequest.ExchangeId;
		flow.RelatedExchangeIds.Add(tokenRequest.ExchangeId);
	}

	static void CaptureIssuedRefreshToken(FlowInProgress flow, Exchange tokenRequest) {
		if (tokenRequest.Response.ResponseJson?.TryGetProperty("refresh_token", out var refreshTokenElement) == true
			&& refreshTokenElement.ValueKind == System.Text.Json.JsonValueKind.String) {
			flow.IssuedRefreshToken = refreshTokenElement.GetString();
		}
	}

	static FlowInProgress? MatchRefreshTokenRequest(Exchange tokenRequest, List<FlowInProgress> flows) {
		string? refreshToken = TryGet(tokenRequest.Request, "refresh_token");
		if (refreshToken is null)
			return null;

		return flows.FirstOrDefault(f => f.IssuedRefreshToken == refreshToken);
	}

	static void CorrelateDeviceCodeFlows(
		List<Exchange> deviceAuthRequests,
		List<Exchange> deviceCodeTokenRequests,
		List<FlowInProgress> flows,
		ref int nextFlowId
	) {
		var deviceCodeByAuthRequest = new Dictionary<int, string?>();
		foreach (var deviceAuthRequest in deviceAuthRequests) {
			string? deviceCode = deviceAuthRequest.Response.ResponseJson?.TryGetProperty("device_code", out var el) == true && el.ValueKind == System.Text.Json.JsonValueKind.String
				? el.GetString()
				: null;
			deviceCodeByAuthRequest[deviceAuthRequest.ExchangeId] = deviceCode;
		}

		var flowsByDeviceCode = new Dictionary<string, FlowInProgress>(StringComparer.Ordinal);

		foreach (var deviceAuthRequest in deviceAuthRequests) {
			var flow = new FlowInProgress { FlowId = $"flow-{nextFlowId++}", FlowType = AuthFlowType.DeviceCode };
			flow.RelatedExchangeIds.Add(deviceAuthRequest.ExchangeId);
			flow.ClientId = TryGet(deviceAuthRequest.Request, "client_id");
			flow.Scopes = SplitScopes(TryGet(deviceAuthRequest.Request, "scope"));
			flows.Add(flow);

			if (deviceCodeByAuthRequest.TryGetValue(deviceAuthRequest.ExchangeId, out string? deviceCode) && deviceCode is not null)
				flowsByDeviceCode[deviceCode] = flow;
		}

		foreach (var pollRequest in deviceCodeTokenRequests) {
			string? deviceCode = TryGet(pollRequest.Request, "device_code");
			if (deviceCode is not null && flowsByDeviceCode.TryGetValue(deviceCode, out var flow)) {
				flow.RelatedExchangeIds.Add(pollRequest.ExchangeId);
				flow.TokenRequestExchangeId = pollRequest.ExchangeId;
				continue;
			}

			var orphan = SeedOrphanFlow(pollRequest, nextFlowId++, "Device-code token poll with no matching device authorization request in capture.");
			orphan.FlowType = AuthFlowType.DeviceCode;
			orphan.TokenRequestExchangeId = pollRequest.ExchangeId;
			flows.Add(orphan);
		}
	}

	static void AttachDiscovery(FlowInProgress flow, IReadOnlyList<Exchange> exchanges) {
		int anchorExchangeId = flow.AuthorizationRequestExchangeId ?? flow.TokenRequestExchangeId ?? flow.RelatedExchangeIds.FirstOrDefault();
		var anchorExchange = exchanges.FirstOrDefault(s => s.ExchangeId == anchorExchangeId);
		if (anchorExchange is null)
			return;

		var priorExchanges = exchanges.Where(s => s.ExchangeId < anchorExchange.ExchangeId).ToList();
		var discovery = EndpointClassifier.FindRelevantDiscovery(anchorExchange, priorExchanges);
		if (discovery is null) {
			flow.AddWarning(FlowWarningKind.MissingDiscoveryDocument, "No OIDC/OAuth2 discovery document observed for this flow; endpoints were inferred heuristically.");
			return;
		}

		flow.Discovery = discovery;
		flow.DiscoveryRequestExchangeId = discovery.SourceExchangeId;
		flow.Issuer = discovery.Issuer;
	}

	static string? TryGet(Request request, string key) {
		return OAuthParameterHelpers.TryGetRequestParameter(request, key, out string value) ? value : null;
	}

	static List<string> SplitScopes(string? scope) {
		return string.IsNullOrWhiteSpace(scope)
			? []
			: scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
	}
}
