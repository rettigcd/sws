namespace Automation;

internal static class RefreshTokenFlowHandler {

	public static async Task<AutomationResult> ExecuteAsync(
		Auth.DetectedAuthenticationFlow flow,
		IReadOnlyList<Session> sessions,
		AutomationOptions options,
		IAuthHttpClient httpClient,
		CancellationToken cancellationToken
	) {
		var stepLog = new AutomationStepLog();
		var variables = new List<ResolvedVariable>();

		var endpoints = EndpointResolver.Resolve(flow, sessions);
		if (string.IsNullOrWhiteSpace(endpoints.TokenEndpoint)) {
			stepLog.Record("Unable to resolve token endpoint for this flow.", success: false);
			return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(UnsupportedFlowReasonKind.MissingRequiredEndpoint, "Could not resolve a token endpoint from discovery, B2C details, or captured sessions."));
		}

		string? refreshToken = options.RefreshTokenOverride
			?? flow.Variables.FirstOrDefault(v => v.Name.Equals("refresh_token", StringComparison.OrdinalIgnoreCase))?.Value;
		if (string.IsNullOrWhiteSpace(refreshToken)) {
			return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(UnsupportedFlowReasonKind.MissingCredentials, "No refresh_token available (not supplied via options and none found on the flow)."));
		}

		variables.Add(new ResolvedVariable(
			"refresh_token",
			refreshToken,
			options.RefreshTokenOverride is not null ? VariableProvenance.CallerSupplied : VariableProvenance.Discovered,
			Auth.VariableCategory.ServerGenerated,
			Notes: options.RefreshTokenOverride is null ? "Fallback to a refresh_token captured in the original capture; may be stale/rotated/expired." : null
		));

		string? clientId = flow.ClientId
			?? flow.Variables.FirstOrDefault(v => v.Name.Equals("client_id", StringComparison.OrdinalIgnoreCase))?.Value;
		if (string.IsNullOrWhiteSpace(clientId))
			return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(UnsupportedFlowReasonKind.MissingCredentials, "Flow has no client_id."));

		return await Execute(flow, endpoints.TokenEndpoint!, clientId, ResolveClientSecret(flow, options), refreshToken, options, httpClient, stepLog, variables, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Standalone refresh, usable without the original flow/sessions once a TokenSet.RefreshToken is known.</summary>
	public static Task<AutomationResult> ExecuteStandaloneAsync(
		string tokenEndpoint,
		string clientId,
		string? clientSecret,
		string refreshToken,
		AutomationOptions options,
		IAuthHttpClient httpClient,
		CancellationToken cancellationToken
	) {
		var stepLog = new AutomationStepLog();
		var variables = new List<ResolvedVariable> {
			new("refresh_token", refreshToken, VariableProvenance.CallerSupplied, Auth.VariableCategory.ServerGenerated),
		};

		var standaloneFlow = new Auth.DetectedAuthenticationFlow(
			"standalone-refresh", Auth.AuthFlowType.RefreshToken, 1.0, [], false, null, null, null, null, null, null,
			[], null, clientId, null, [], null, [], [], []
		);

		return Execute(standaloneFlow, tokenEndpoint, clientId, clientSecret, refreshToken, options, httpClient, stepLog, variables, cancellationToken);
	}

	static async Task<AutomationResult> Execute(
		Auth.DetectedAuthenticationFlow flow,
		string tokenEndpoint,
		string clientId,
		string? clientSecret,
		string refreshToken,
		AutomationOptions options,
		IAuthHttpClient httpClient,
		AutomationStepLog stepLog,
		List<ResolvedVariable> variables,
		CancellationToken cancellationToken
	) {
		var scopes = options.ScopesOverride ?? flow.Scopes;
		var formFields = new Dictionary<string, string> {
			["grant_type"] = "refresh_token",
			["refresh_token"] = refreshToken,
			["client_id"] = clientId,
		};
		if (scopes.Count > 0)
			formFields["scope"] = string.Join(' ', scopes);
		if (!string.IsNullOrWhiteSpace(clientSecret))
			formFields["client_secret"] = clientSecret;

		var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) {
			Content = new FormUrlEncodedContent(formFields),
		};
		HeaderHelpers.Apply(request);

		stepLog.Record("Sent refresh_token request to token endpoint.", requestUrl: tokenEndpoint);
		var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

		return await TokenExchange.ParseResponseAsync(flow, stepLog, variables, httpClient, response, cancellationToken).ConfigureAwait(false);
	}

	static string? ResolveClientSecret(Auth.DetectedAuthenticationFlow flow, AutomationOptions options) {
		return options.ClientSecretOverride
			?? flow.Variables.FirstOrDefault(v => v.Category == Auth.VariableCategory.Secret && v.Name.Equals("client_secret", StringComparison.OrdinalIgnoreCase))?.Value;
	}

	static AutomationResult Failure(
		Auth.DetectedAuthenticationFlow flow,
		AutomationStepLog stepLog,
		List<ResolvedVariable> variables,
		IAuthHttpClient httpClient,
		UnsupportedFlowReason? reason = null,
		string? errorMessage = null
	) {
		return new AutomationResult(false, flow.FlowId, flow.FlowType, null, TokenExchange.ExtractCookies(httpClient), [], stepLog.ToList(), variables, reason, errorMessage);
	}
}
