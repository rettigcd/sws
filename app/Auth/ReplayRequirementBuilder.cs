namespace Auth;

internal static class ReplayRequirementBuilder {

	public static List<ReplayRequirement> Build(FlowInProgress flow, List<Variable> variables, bool isAzureB2c) {
		var requirements = new List<ReplayRequirement>();

		if (flow.Discovery is not null)
			requirements.Add(new ReplayRequirement(ReplayRequirementKind.UseDiscoveredEndpoint, "Use the authorization/token endpoints from the discovery document rather than hardcoded URLs."));

		switch (flow.FlowType) {
			case AuthFlowType.AuthorizationCode:
			case AuthFlowType.AuthorizationCodeWithPkce:
			case AuthFlowType.Implicit:
			case AuthFlowType.Hybrid:
				requirements.Add(new ReplayRequirement(ReplayRequirementKind.GenerateState, "Generate a new state value per run.", "state"));
				if (variables.Any(v => v.Name.Equals("nonce", StringComparison.OrdinalIgnoreCase)))
					requirements.Add(new ReplayRequirement(ReplayRequirementKind.GenerateNonce, "Generate a new nonce value per run.", "nonce"));
				requirements.Add(new ReplayRequirement(ReplayRequirementKind.PreserveClientId, "Preserve the configured client_id.", "client_id"));
				requirements.Add(new ReplayRequirement(ReplayRequirementKind.PreserveRedirectUri, "Preserve the configured redirect_uri.", "redirect_uri"));
				if (flow.Scopes.Count > 0)
					requirements.Add(new ReplayRequirement(ReplayRequirementKind.PreserveScopes, "Preserve the configured scopes.", "scope"));
				requirements.Add(new ReplayRequirement(ReplayRequirementKind.DoNotReuseAuthorizationCode, "Do not reuse the observed authorization code; obtain a new one per run.", "code"));
				requirements.Add(new ReplayRequirement(ReplayRequirementKind.RequireInteractiveLogin, "Browser-interactive login may be required to complete authentication."));
				break;

			case AuthFlowType.ClientCredentials:
				requirements.Add(new ReplayRequirement(ReplayRequirementKind.PreserveClientId, "Preserve the configured client_id.", "client_id"));
				requirements.Add(new ReplayRequirement(ReplayRequirementKind.RequireClientSecret, "Token endpoint requires a client secret or client assertion.", "client_secret"));
				break;

			case AuthFlowType.RefreshToken:
				requirements.Add(new ReplayRequirement(ReplayRequirementKind.Other, "Use a currently-valid refresh_token; do not reuse the one observed in this capture.", "refresh_token"));
				break;

			case AuthFlowType.DeviceCode:
				requirements.Add(new ReplayRequirement(ReplayRequirementKind.RequireInteractiveLogin, "User must complete device-code verification out of band (enter user_code at the verification URL).", "user_code"));
				break;

			case AuthFlowType.ResourceOwnerPasswordCredentials:
				requirements.Add(new ReplayRequirement(ReplayRequirementKind.Other, "Requires end-user username/password credentials."));
				break;
		}

		if (variables.Any(v => v.Category == VariableCategory.Derived && v.Name.Equals("code_challenge", StringComparison.OrdinalIgnoreCase))) {
			requirements.Add(new ReplayRequirement(ReplayRequirementKind.GenerateCodeVerifier, "Generate a new code_verifier per run.", "code_verifier"));
			requirements.Add(new ReplayRequirement(ReplayRequirementKind.DeriveCodeChallenge, "Derive code_challenge from code_verifier via SHA-256 + Base64URL when code_challenge_method=S256.", "code_challenge"));
			requirements.Add(new ReplayRequirement(ReplayRequirementKind.RequireCodeVerifier, "Token endpoint requires code_verifier for PKCE flows.", "code_verifier"));
		}

		if (isAzureB2c)
			requirements.Add(new ReplayRequirement(ReplayRequirementKind.DoNotReuseTransientCookies, "Do not reuse observed B2C transaction/session cookies (x-ms-cpim-*); they are transient per-flow state."));

		return requirements;
	}
}
