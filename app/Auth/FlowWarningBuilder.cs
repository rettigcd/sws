namespace Auth;

/// <summary>
/// Appends analysis-based warnings (beyond the structural ones FlowCorrelator already raised)
/// once a flow's variables have been extracted.
/// </summary>
internal static class FlowWarningBuilder {

	public static void AppendAnalysisWarnings(FlowInProgress flow, List<Variable> variables) {
		if (flow.FlowType is AuthFlowType.Implicit or AuthFlowType.Hybrid) {
			flow.AddWarning(FlowWarningKind.UnsafeImplicitFlow, "Implicit/hybrid flow returns tokens directly in the URL fragment, which is less secure than authorization code + PKCE.");
		}

		var hasChallenge = variables.Any(v => v.Name.Equals("code_challenge", StringComparison.OrdinalIgnoreCase));
		var hasVerifier = variables.Any(v => v.Name.Equals("code_verifier", StringComparison.OrdinalIgnoreCase));
		if (hasChallenge && !hasVerifier)
			flow.AddWarning(FlowWarningKind.PkceMismatch, "code_challenge observed on the authorization request but no matching code_verifier observed on the token request.");

		if (flow.FlowType == AuthFlowType.ClientCredentials && !variables.Any(v => v.Category == VariableCategory.Secret))
			flow.AddWarning(FlowWarningKind.MissingClientSecret, "client_credentials grant observed with no client_secret, client_assertion, or Basic-auth credential.");

		foreach (var tokenVariable in variables.Where(v =>
			v.Source == VariableSource.RedirectUrlParameter
			&& (v.Name.Equals("access_token", StringComparison.OrdinalIgnoreCase) || v.Name.Equals("id_token", StringComparison.OrdinalIgnoreCase))
		)) {
			flow.AddWarning(FlowWarningKind.SensitiveTokenExposure, $"{tokenVariable.Name} was exposed directly in a URL/fragment.", tokenVariable.SessionId);
		}

		if (variables.Any(v => v.Name.Contains("captcha", StringComparison.OrdinalIgnoreCase) || v.Name.Contains("mfa", StringComparison.OrdinalIgnoreCase)))
			flow.AddWarning(FlowWarningKind.MfaOrCaptchaSuspected, "Flow data suggests MFA or CAPTCHA may be required, which can prevent full automated replay.");
	}
}
