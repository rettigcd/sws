using Automation;

namespace Minimal;

// Holds all variables required to complete B2C authentication
// Passed to static Steps methods.
// Holds results from the step.
internal class Context {

	// Set after instantiation, before any Steps methods are called.
	public IAuthHttpClient? Http { get; set; }

	// The tenant/policy-specific discovery URL to fetch, e.g.
	// https://{tenant}.b2clogin.com/{tenant}.onmicrosoft.com/{policy}/v2.0/.well-known/openid-configuration
	// Set after instantiation, before LoadWellKnownConfigurationAsync is called.
	public string? WellKnownEndpoint { get; set; }

	public WellKnownConfiguration? WellKnown { get; set; }

	// Caller-supplied app registration values needed to build the authorization request.
	// Set after instantiation, before SendAuthorizationRequestAsync is called.
	public string? ClientId { get; set; }
	public string? RedirectUri { get; set; }
	public string? Scope { get; set; }

	// Generated fresh per authorization attempt (see Crypto), not caller-supplied.
	public string? State { get; set; }
	public string? CodeVerifier { get; set; }
	public string? CodeChallenge { get; set; }

	// Caller-supplied login-form credentials, used only when SendAuthorizationRequestAsync
	// returns StepResult.LoginPage. Set after instantiation, before LoginWithUsernameAndPasswordAsync is called.
	public string? Username { get; set; }
	public string? Password { get; set; }

	// The login page HTML/URL captured when SendAuthorizationRequestAsync returns StepResult.LoginPage,
	// so LoginWithUsernameAndPasswordAsync can parse and submit it without re-fetching.
	public string? LoginPageHtml { get; set; }
	public string? LoginPageUrl { get; set; }

	// The authorization code extracted from the IdP's response back to redirect_uri
	// (via redirect query, redirect fragment, or form_post), regardless of response_mode.
	public string? Code { get; set; }

	// Populated by ExchangeCodeForTokensAsync.
	public TokenResponse? Tokens { get; set; }

}