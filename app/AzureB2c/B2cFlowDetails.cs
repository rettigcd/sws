namespace AzureB2c;

/// <summary>
/// Azure AD B2C-specific enrichment attached to a generically-detected Auth.DetectedAuthenticationFlow.
/// </summary>
internal sealed record B2cFlowDetails(
	string? Tenant,
	string? Policy,
	string? AuthorityBaseUrl,
	string? AuthorizationEndpoint,
	string? TokenEndpoint,
	string? RedirectUri,
	string? ClientId,
	string? ResponseMode,
	string? ResponseType,
	List<string> Scopes,
	string? CodeChallenge,
	string? CodeChallengeMethod,
	Dictionary<string, string> B2cCookies
);
