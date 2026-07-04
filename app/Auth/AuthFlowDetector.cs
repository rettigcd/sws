namespace Auth;

/// <summary>
/// Top-level entry point: detects and correlates OIDC/OAuth2/Azure-B2C authentication flows
/// from a session capture. This is the "detector/analyzer" component only; a future replay
/// engine (not built here) is expected to consume AuthFlowDetectionResult.Flows.
/// </summary>
internal static class AuthFlowDetector {

	public static AuthFlowDetectionResult Detect(IReadOnlyList<Session> sessions) {
		var classifiedSessions = SessionClassifier.ClassifyUnknownSessions(sessions);
		var flowsInProgress = FlowCorrelator.Correlate(classifiedSessions);

		var flows = new List<DetectedAuthenticationFlow>();
		foreach (var flow in flowsInProgress) {
			var variables = VariableExtractor.Extract(flow, classifiedSessions);
			(bool isAzureB2c, AzureB2c.B2cFlowDetails? b2cDetails) = AzureB2c.B2cEnricher.Enrich(flow, classifiedSessions);
			var replayRequirements = ReplayRequirementBuilder.Build(flow, variables, isAzureB2c);
			FlowWarningBuilder.AppendAnalysisWarnings(flow, variables);
			var authenticationMethod = DetermineAuthenticationMethod(flow, variables, classifiedSessions);

			flows.Add(new DetectedAuthenticationFlow(
				flow.FlowId,
				flow.FlowType,
				flow.Confidence,
				flow.ConfidenceReasons,
				isAzureB2c,
				b2cDetails,
				flow.Discovery,
				flow.DiscoveryRequestSessionId,
				flow.AuthorizationRequestSessionId,
				flow.AuthorizationCallbackSessionId,
				flow.TokenRequestSessionId,
				[.. flow.RelatedSessionIds.OrderBy(id => id)],
				flow.Issuer,
				flow.ClientId,
				flow.RedirectUri,
				flow.Scopes,
				authenticationMethod,
				variables,
				replayRequirements,
				flow.Warnings
			));
		}

		var discoveryDocuments = classifiedSessions
			.Where(s => s.Request.RequestType == RequestType.Configuration)
			.Select(DiscoveryDocumentParser.TryParse)
			.Where(doc => doc is not null)
			.Select(doc => doc!)
			.ToList();

		var sessionClassifications = classifiedSessions
			.Where(s => s.Request.RequestType != RequestType.Unknown)
			.Select(s => new RequestClassification(s.SessionId, s.Request.RequestType, s.Response.ResponseClassification))
			.ToList();

		return new AuthFlowDetectionResult(
			DateTimeOffset.UtcNow,
			[.. flows.OrderBy(f => f.RelatedSessionIds.Count > 0 ? f.RelatedSessionIds.Min() : int.MaxValue)],
			discoveryDocuments,
			sessionClassifications,
			[]
		);
	}

	static AuthenticationCredentials? DetermineAuthenticationMethod(FlowInProgress flow, List<Variable> variables, IReadOnlyList<Session> allSessions) {
		var callbackSession = allSessions.FirstOrDefault(s => s.SessionId == flow.AuthorizationCallbackSessionId);
		if (callbackSession?.Request.FormBody is { Count: > 0 } formBody) {
			string? username = formBody
				.FirstOrDefault(e => e.Key.Equals("username", StringComparison.OrdinalIgnoreCase)
					|| e.Key.Equals("email", StringComparison.OrdinalIgnoreCase)
					|| e.Key.Equals("logonIdentifier", StringComparison.OrdinalIgnoreCase))
				?.Value;

			string? password = formBody
				.FirstOrDefault(e => e.Key.Equals("password", StringComparison.OrdinalIgnoreCase))
				?.Value;

			if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
				return new UsernamePasswordCredentials(username, password);
		}

		var sessionCookieVariable = variables.FirstOrDefault(v => v.Source == VariableSource.Cookie && v.Category == VariableCategory.ServerGenerated);
		if (sessionCookieVariable is not null)
			return new SessionCookieCredentials(sessionCookieVariable.Name, sessionCookieVariable.Value);

		return null;
	}
}
