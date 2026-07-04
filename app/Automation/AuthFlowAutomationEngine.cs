namespace Automation;

/// <summary>
/// Public entry point for executing a detected authentication flow (component 2, per docs/AUTH_FLOW_AUTOMATER_SPEC.md). 
/// v1 supports AuthorizationCode/AuthorizationCodeWithPkce and RefreshToken only; 
/// every other Auth.AuthFlowType fails clearly with UnsupportedFlowReasonKind.UnsupportedFlowType and makes zero HTTP calls.
///
/// There is no environment allowlist/guardrail - the engine will hit whatever host the flow points at. 
/// The only safety boundary is that tests must only ever wire up a fake IAuthHttpClient, never SystemNetAuthHttpClient.
/// </summary>
internal static class AuthFlowAutomationEngine {

	public static async Task<AutomationResult> ExecuteAsync(
		Auth.DetectedAuthenticationFlow flow,
		IReadOnlyList<Session> sessions,
		AutomationOptions? options = null,
		CancellationToken cancellationToken = default
	) {
		options ??= new AutomationOptions();
		bool ownsHttpClient = options.HttpClient is null;
		var httpClient = options.HttpClient ?? new SystemNetAuthHttpClient(options.HttpTimeout);

		try {
			return flow.FlowType switch {
				Auth.AuthFlowType.AuthorizationCode or Auth.AuthFlowType.AuthorizationCodeWithPkce =>
					await AuthorizationCodeFlowHandler.ExecuteAsync(flow, sessions, options, httpClient, cancellationToken).ConfigureAwait(false),

				Auth.AuthFlowType.RefreshToken =>
					await RefreshTokenFlowHandler.ExecuteAsync(flow, sessions, options, httpClient, cancellationToken).ConfigureAwait(false),

				_ => Unsupported(flow, httpClient),
			};
		}
		finally {
			if (ownsHttpClient && httpClient is IDisposable disposable)
				disposable.Dispose();
		}
	}

	/// <summary>
	/// Refreshes an access token using a refresh_token obtained from a prior ExecuteAsync call,
	/// without needing the original flow/sessions again.
	/// </summary>
	public static async Task<AutomationResult> RefreshAccessTokenAsync(
		string tokenEndpoint,
		string clientId,
		string? clientSecret,
		string refreshToken,
		AutomationOptions? options = null,
		CancellationToken cancellationToken = default
	) {
		options ??= new AutomationOptions();
		bool ownsHttpClient = options.HttpClient is null;
		var httpClient = options.HttpClient ?? new SystemNetAuthHttpClient(options.HttpTimeout);

		try {
			return await RefreshTokenFlowHandler.ExecuteStandaloneAsync(tokenEndpoint, clientId, clientSecret, refreshToken, options, httpClient, cancellationToken).ConfigureAwait(false);
		}
		finally {
			if (ownsHttpClient && httpClient is IDisposable disposable)
				disposable.Dispose();
		}
	}

	static AutomationResult Unsupported(Auth.DetectedAuthenticationFlow flow, IAuthHttpClient httpClient) {
		return new AutomationResult(
			false,
			flow.FlowId,
			flow.FlowType,
			null,
			[],
			[],
			[],
			[],
			new UnsupportedFlowReason(UnsupportedFlowReasonKind.UnsupportedFlowType, $"v1 does not support {flow.FlowType}."),
			null
		);
	}
}
