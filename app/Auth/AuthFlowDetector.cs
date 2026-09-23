namespace Auth;

using Saz;

/// <summary>
/// Top-level entry point: detects and correlates OIDC/OAuth2/Azure-B2C authentication flows
/// from an exchange capture. This is the "detector/analyzer" component only; a future replay
/// engine (not built here) is expected to consume AuthFlowDetectionResult.Flows.
/// </summary>
internal static class AuthFlowDetector {

	public static AuthFlowDetectionResult Detect(IReadOnlyList<Exchange> exchanges) {
		var classifiedExchanges = ExchangeClassifier.Classify(exchanges);
		var flowsInProgress = FlowCorrelator.Correlate(classifiedExchanges);

		var flows = new List<DetectedAuthenticationFlow>();
		foreach (var flow in flowsInProgress) {
			var variables = VariableExtractor.Extract(flow, exchanges);
			(bool isAzureB2c, AzureB2c.B2cFlowDetails? b2cDetails) = AzureB2c.B2cEnricher.Enrich(flow, exchanges);
			var replayRequirements = ReplayRequirementBuilder.Build(flow, variables, isAzureB2c);
			FlowWarningBuilder.AppendAnalysisWarnings(flow, variables);
			var authenticationMethod = DetermineAuthenticationMethod(flow, variables, exchanges);

			flows.Add(new DetectedAuthenticationFlow(
				flow.FlowId,
				flow.FlowType,
				flow.Confidence,
				flow.ConfidenceReasons,
				isAzureB2c,
				b2cDetails,
				flow.Discovery,
				flow.DiscoveryRequestExchangeId,
				flow.AuthorizationRequestExchangeId,
				flow.AuthorizationCallbackExchangeId,
				flow.TokenRequestExchangeId,
				[.. flow.RelatedExchangeIds.OrderBy(id => id)],
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

		var discoveryDocuments = classifiedExchanges
			.Where(c => c.RequestType == RequestType.Configuration)
			.Select(c => DiscoveryDocumentParser.TryParse(c.Exchange))
			.Where(doc => doc is not null)
			.Select(doc => doc!)
			.ToList();

		var exchangeClassifications = classifiedExchanges
			.Where(c => c.RequestType != RequestType.Unknown)
			.Select(c => new RequestClassification(c.ExchangeId, c.RequestType, c.ResponseType))
			.ToList();

		return new AuthFlowDetectionResult(
			DateTimeOffset.UtcNow,
			[.. flows.OrderBy(f => f.RelatedExchangeIds.Count > 0 ? f.RelatedExchangeIds.Min() : int.MaxValue)],
			discoveryDocuments,
			exchangeClassifications,
			[]
		);
	}

	static AuthenticationCredentials? DetermineAuthenticationMethod(FlowInProgress flow, List<Variable> variables, IReadOnlyList<Exchange> allExchanges) {
		var callbackExchange = allExchanges.FirstOrDefault(s => s.ExchangeId == flow.AuthorizationCallbackExchangeId);
		if (callbackExchange?.Request.FormBody is { Count: > 0 } formBody) {
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
