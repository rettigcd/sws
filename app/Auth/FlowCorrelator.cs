namespace Auth;

/// <summary>
/// Mutable in-progress flow used while correlating sessions. Converted to an immutable
/// DetectedAuthenticationFlow once variables/replay-requirements/warnings have been attached.
/// </summary>
internal sealed class FlowInProgress {
	public required string FlowId { get; init; }
	public AuthFlowType FlowType { get; set; } = AuthFlowType.Unknown;
	public double Confidence { get; set; } = 1.0;
	public List<string> ConfidenceReasons { get; } = [];

	public OidcDiscoveryDocument? Discovery { get; set; }
	public int? DiscoveryRequestSessionId { get; set; }
	public int? AuthorizationRequestSessionId { get; set; }
	public int? AuthorizationCallbackSessionId { get; set; }
	public int? TokenRequestSessionId { get; set; }
	public List<int> RelatedSessionIds { get; } = [];

	public string? Issuer { get; set; }
	public string? ClientId { get; set; }
	public string? RedirectUri { get; set; }
	public List<string> Scopes { get; set; } = [];

	public string? CapturedState { get; set; }
	public string? CapturedCode { get; set; }
	public string? CapturedDeviceCode { get; set; }
	public string? IssuedRefreshToken { get; set; }

	public List<FlowWarning> Warnings { get; } = [];

	public void AddWarning(FlowWarningKind kind, string message, params int[] relatedSessionIds) {
		Warnings.Add(new FlowWarning(kind, message, relatedSessionIds.Length > 0 ? relatedSessionIds : [.. RelatedSessionIds]));
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

	public static List<FlowInProgress> Correlate(IReadOnlyList<Session> sessions) {
		var flows = new List<FlowInProgress>();
		var nextFlowId = 1;

		var authRequests = sessions.Where(s => AuthorizationRequestTypes.Contains(s.Request.RequestType)).ToList();
		var callbacks = sessions.Where(s => s.Request.RequestType == RequestType.AuthorizationCallbackRequest).ToList();
		var authCodeTokenRequests = sessions.Where(s => s.Request.RequestType == RequestType.AuthorizationCodeTokenRequest).ToList();
		var refreshTokenRequests = sessions.Where(s => s.Request.RequestType == RequestType.RefreshTokenRequest).ToList();
		var clientCredentialTokenRequests = sessions.Where(s => s.Request.RequestType == RequestType.ClientCredentialsTokenRequest).ToList();
		var passwordTokenRequests = sessions.Where(s => s.Request.RequestType == RequestType.PasswordTokenRequest).ToList();
		var deviceCodeTokenRequests = sessions.Where(s => s.Request.RequestType == RequestType.DeviceCodeTokenRequest).ToList();
		var deviceAuthRequests = sessions.Where(s => s.Request.RequestType == RequestType.AuthorizationRequest_DeviceAuthorization).ToList();

		// Step 3: seed one flow per authorization request.
		var flowByAuthRequestId = new Dictionary<int, FlowInProgress>();
		foreach (var authRequest in authRequests) {
			var flow = SeedFlowFromAuthorizationRequest(authRequest, nextFlowId++);
			flowByAuthRequestId[authRequest.SessionId] = flow;
			flows.Add(flow);
		}

		// Step 4: match callbacks -> authorization requests.
		var usedAuthRequestIds = new HashSet<int>();
		foreach (var callback in callbacks) {
			var match = MatchCallbackToAuthRequest(callback, authRequests, usedAuthRequestIds);
			if (match is null) {
				var orphan = SeedOrphanFlow(callback, nextFlowId++, "Authorization callback with no matching authorization request in capture.");
				orphan.AuthorizationCallbackSessionId = callback.SessionId;
				flows.Add(orphan);
				continue;
			}

			var (authRequest, reason, confidenceDelta) = match.Value;
			usedAuthRequestIds.Add(authRequest.SessionId);
			var flow = flowByAuthRequestId[authRequest.SessionId];
			AttachCallback(flow, callback, reason, confidenceDelta);
		}

		foreach (var authRequest in authRequests) {
			var flow = flowByAuthRequestId[authRequest.SessionId];
			if (flow.AuthorizationCallbackSessionId is null)
				flow.AddWarning(FlowWarningKind.MissingCallback, "No authorization callback observed for this authorization request.", authRequest.SessionId);
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
			if (flow.AuthorizationCallbackSessionId is not null && flow.TokenRequestSessionId is null
				&& flow.FlowType is AuthFlowType.AuthorizationCode or AuthFlowType.AuthorizationCodeWithPkce) {
				flow.AddWarning(FlowWarningKind.MissingTokenExchange, "Authorization callback observed but no token exchange followed.");
			}
		}

		foreach (var tokenRequest in refreshTokenRequests) {
			var flow = MatchRefreshTokenRequest(tokenRequest, flows);
			if (flow is not null) {
				flow.RelatedSessionIds.Add(tokenRequest.SessionId);
				continue;
			}

			var orphan = SeedOrphanFlow(tokenRequest, nextFlowId++, "Refresh-token request with no originating flow found in capture.");
			orphan.FlowType = AuthFlowType.RefreshToken;
			orphan.TokenRequestSessionId = tokenRequest.SessionId;
			flows.Add(orphan);
		}

		foreach (var tokenRequest in clientCredentialTokenRequests) {
			var flow = SeedStandaloneFlow(tokenRequest, nextFlowId++);
			flow.FlowType = AuthFlowType.ClientCredentials;
			flow.TokenRequestSessionId = tokenRequest.SessionId;
			flow.ClientId = TryGet(tokenRequest.Request, "client_id");
			flow.Scopes = SplitScopes(TryGet(tokenRequest.Request, "scope"));
			flows.Add(flow);
		}

		foreach (var tokenRequest in passwordTokenRequests) {
			var flow = SeedStandaloneFlow(tokenRequest, nextFlowId++);
			flow.FlowType = AuthFlowType.ResourceOwnerPasswordCredentials;
			flow.TokenRequestSessionId = tokenRequest.SessionId;
			flow.ClientId = TryGet(tokenRequest.Request, "client_id");
			flow.Scopes = SplitScopes(TryGet(tokenRequest.Request, "scope"));
			flows.Add(flow);
		}

		CorrelateDeviceCodeFlows(deviceAuthRequests, deviceCodeTokenRequests, flows, ref nextFlowId);

		// Step 6: attach discovery documents.
		foreach (var flow in flows)
			AttachDiscovery(flow, sessions);

		return flows;
	}

	static FlowInProgress SeedFlowFromAuthorizationRequest(Session authRequest, int flowId) {
		var flow = new FlowInProgress { FlowId = $"flow-{flowId}" };
		flow.AuthorizationRequestSessionId = authRequest.SessionId;
		flow.RelatedSessionIds.Add(authRequest.SessionId);
		flow.ClientId = TryGet(authRequest.Request, "client_id");
		flow.RedirectUri = TryGet(authRequest.Request, "redirect_uri");
		flow.Scopes = SplitScopes(TryGet(authRequest.Request, "scope"));
		flow.CapturedState = TryGet(authRequest.Request, "state");
		flow.FlowType = authRequest.Request.RequestType switch {
			RequestType.AuthorizationRequest_AuthCodeWithPKCE => AuthFlowType.AuthorizationCodeWithPkce,
			RequestType.AuthorizationRequest_AuthCode => AuthFlowType.AuthorizationCode,
			RequestType.AuthorizationRequest_Implicit => AuthFlowType.Implicit,
			RequestType.AuthorizationRequest_Hybrid => AuthFlowType.Hybrid,
			_ => AuthFlowType.Unknown,
		};

		return flow;
	}

	static FlowInProgress SeedOrphanFlow(Session session, int flowId, string warningMessage) {
		var flow = new FlowInProgress { FlowId = $"flow-{flowId}" };
		flow.RelatedSessionIds.Add(session.SessionId);
		flow.Confidence = 0.3;
		flow.ConfidenceReasons.Add("Single unmatched session; no correlation performed.");
		flow.AddWarning(FlowWarningKind.IncompleteFlow, warningMessage, session.SessionId);
		return flow;
	}

	static FlowInProgress SeedStandaloneFlow(Session session, int flowId) {
		var flow = new FlowInProgress { FlowId = $"flow-{flowId}" };
		flow.RelatedSessionIds.Add(session.SessionId);
		flow.ConfidenceReasons.Add("Self-contained grant type; no cross-session correlation required.");
		return flow;
	}

	static (Session AuthRequest, string Reason, double ConfidenceDelta)? MatchCallbackToAuthRequest(
		Session callback,
		List<Session> authRequests,
		HashSet<int> usedAuthRequestIds
	) {
		var priorCandidates = authRequests
			.Where(a => a.SessionId < callback.SessionId && !usedAuthRequestIds.Contains(a.SessionId))
			.OrderByDescending(a => a.SessionId)
			.ToList();

		if (priorCandidates.Count == 0)
			return null;

		var callbackState = callback.Request.QueryParameters.TryGetValue("state", out var qsState) ? qsState : null;
		if (callbackState is null
			&& OAuthParameterHelpers.TryParseFragmentParameters(callback.Request.Fragment, out var fragmentParams)
			&& fragmentParams.TryGetValue("state", out var fragState)) {
			callbackState = fragState;
		}

		if (callbackState is not null) {
			var stateMatch = priorCandidates.FirstOrDefault(a => TryGet(a.Request, "state") == callbackState);
			if (stateMatch is not null)
				return (stateMatch, "state match", 0.0);
		}

		foreach (var candidate in priorCandidates) {
			var redirectUri = TryGet(candidate.Request, "redirect_uri");
			if (redirectUri is not null && OAuthParameterHelpers.UrlsMatchIgnoringFragment(redirectUri, callback.Request.Url))
				return (candidate, "redirect_uri + sequence match", 0.3);
		}

		var host = Uri.TryCreate(callback.Request.Url, UriKind.Absolute, out var callbackUri) ? callbackUri.Host : null;
		var sequenceMatch = priorCandidates.FirstOrDefault(a =>
			host is null || (Uri.TryCreate(a.Request.Url, UriKind.Absolute, out var authUri) && authUri.Host == host)
		) ?? priorCandidates[0];

		return (sequenceMatch, "sequence-only fallback", 0.6);
	}

	static void AttachCallback(FlowInProgress flow, Session callback, string reason, double confidenceDelta) {
		flow.AuthorizationCallbackSessionId = callback.SessionId;
		flow.RelatedSessionIds.Add(callback.SessionId);
		flow.ReduceConfidence(confidenceDelta, reason);

		var code = callback.Request.QueryParameters.TryGetValue("code", out var qsCode) ? qsCode : null;
		if (code is null
			&& OAuthParameterHelpers.TryParseFragmentParameters(callback.Request.Fragment, out var fragmentParams)
			&& fragmentParams.TryGetValue("code", out var fragCode)) {
			code = fragCode;
		}

		flow.CapturedCode = code;
	}

	static FlowInProgress? MatchAuthorizationCodeTokenRequest(Session tokenRequest, List<FlowInProgress> flows) {
		var code = TryGet(tokenRequest.Request, "code");
		if (code is not null) {
			var exact = flows.FirstOrDefault(f => f.CapturedCode == code && f.TokenRequestSessionId is null);
			if (exact is not null)
				return exact;
		}

		return flows
			.Where(f => f.TokenRequestSessionId is null
				&& f.AuthorizationCallbackSessionId is not null
				&& f.AuthorizationCallbackSessionId < tokenRequest.SessionId
				&& f.FlowType is AuthFlowType.AuthorizationCode or AuthFlowType.AuthorizationCodeWithPkce)
			.OrderByDescending(f => f.AuthorizationCallbackSessionId)
			.FirstOrDefault();
	}

	static void AttachTokenRequest(FlowInProgress flow, Session tokenRequest) {
		flow.TokenRequestSessionId = tokenRequest.SessionId;
		flow.RelatedSessionIds.Add(tokenRequest.SessionId);
	}

	static void CaptureIssuedRefreshToken(FlowInProgress flow, Session tokenRequest) {
		if (tokenRequest.Response.ResponseJson?.TryGetProperty("refresh_token", out var refreshTokenElement) == true
			&& refreshTokenElement.ValueKind == System.Text.Json.JsonValueKind.String) {
			flow.IssuedRefreshToken = refreshTokenElement.GetString();
		}
	}

	static FlowInProgress? MatchRefreshTokenRequest(Session tokenRequest, List<FlowInProgress> flows) {
		var refreshToken = TryGet(tokenRequest.Request, "refresh_token");
		if (refreshToken is null)
			return null;

		return flows.FirstOrDefault(f => f.IssuedRefreshToken == refreshToken);
	}

	static void CorrelateDeviceCodeFlows(
		List<Session> deviceAuthRequests,
		List<Session> deviceCodeTokenRequests,
		List<FlowInProgress> flows,
		ref int nextFlowId
	) {
		var deviceCodeByAuthRequest = new Dictionary<int, string?>();
		foreach (var deviceAuthRequest in deviceAuthRequests) {
			var deviceCode = deviceAuthRequest.Response.ResponseJson?.TryGetProperty("device_code", out var el) == true && el.ValueKind == System.Text.Json.JsonValueKind.String
				? el.GetString()
				: null;
			deviceCodeByAuthRequest[deviceAuthRequest.SessionId] = deviceCode;
		}

		var flowsByDeviceCode = new Dictionary<string, FlowInProgress>(StringComparer.Ordinal);

		foreach (var deviceAuthRequest in deviceAuthRequests) {
			var flow = new FlowInProgress { FlowId = $"flow-{nextFlowId++}", FlowType = AuthFlowType.DeviceCode };
			flow.RelatedSessionIds.Add(deviceAuthRequest.SessionId);
			flow.ClientId = TryGet(deviceAuthRequest.Request, "client_id");
			flow.Scopes = SplitScopes(TryGet(deviceAuthRequest.Request, "scope"));
			flows.Add(flow);

			if (deviceCodeByAuthRequest.TryGetValue(deviceAuthRequest.SessionId, out var deviceCode) && deviceCode is not null)
				flowsByDeviceCode[deviceCode] = flow;
		}

		foreach (var pollRequest in deviceCodeTokenRequests) {
			var deviceCode = TryGet(pollRequest.Request, "device_code");
			if (deviceCode is not null && flowsByDeviceCode.TryGetValue(deviceCode, out var flow)) {
				flow.RelatedSessionIds.Add(pollRequest.SessionId);
				flow.TokenRequestSessionId = pollRequest.SessionId;
				continue;
			}

			var orphan = SeedOrphanFlow(pollRequest, nextFlowId++, "Device-code token poll with no matching device authorization request in capture.");
			orphan.FlowType = AuthFlowType.DeviceCode;
			orphan.TokenRequestSessionId = pollRequest.SessionId;
			flows.Add(orphan);
		}
	}

	static void AttachDiscovery(FlowInProgress flow, IReadOnlyList<Session> sessions) {
		var anchorSessionId = flow.AuthorizationRequestSessionId ?? flow.TokenRequestSessionId ?? flow.RelatedSessionIds.FirstOrDefault();
		var anchorSession = sessions.FirstOrDefault(s => s.SessionId == anchorSessionId);
		if (anchorSession is null)
			return;

		var priorSessions = sessions.Where(s => s.SessionId < anchorSession.SessionId).ToList();
		var discovery = EndpointClassifier.FindRelevantDiscovery(anchorSession, priorSessions);
		if (discovery is null) {
			flow.AddWarning(FlowWarningKind.MissingDiscoveryDocument, "No OIDC/OAuth2 discovery document observed for this flow; endpoints were inferred heuristically.");
			return;
		}

		flow.Discovery = discovery;
		flow.DiscoveryRequestSessionId = discovery.SourceSessionId;
		flow.Issuer = discovery.Issuer;
	}

	static string? TryGet(Request request, string key) {
		return OAuthParameterHelpers.TryGetRequestParameter(request, key, out var value) ? value : null;
	}

	static List<string> SplitScopes(string? scope) {
		return string.IsNullOrWhiteSpace(scope)
			? []
			: scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
	}
}
