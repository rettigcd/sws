namespace Automation;

/// <summary>Caller-supplied overrides. Anything left null falls back to values discovered from the flow.</summary>
internal sealed record AutomationOptions(
	IAuthHttpClient? HttpClient = null,
	string? UsernameOverride = null,
	string? PasswordOverride = null,
	string? ClientSecretOverride = null,
	string? RedirectUriOverride = null,
	List<string>? ScopesOverride = null,
	string? RefreshTokenOverride = null,
	int MaxRedirects = 10,
	int MaxLoginPageHops = 5,
	TimeSpan? HttpTimeout = null
);
