using System.Net;
using Automation;

namespace Minimal;

public class AuthSession {

	Context _context = new();

	public AuthSession(IAuthHttpClient http) {
		_context.Http = http;
	}

	public void InitAuthorizationRequest(string wellKnownEndpoint, string clientId, string redirectUri, string scope) {
		_context.WellKnownEndpoint = wellKnownEndpoint;
		_context.ClientId = clientId;
		_context.RedirectUri = redirectUri;
		_context.Scope = scope;
	}

	// Seeds active session cookies from the auth server (e.g. B2C SSO cookie) onto the HTTP
	// client before the flow starts, so SendAuthorizationRequestAsync may redirect immediately
	// without needing InitAuthorization's username/password at all.
	public void InitAuthServerCookies(IEnumerable<Cookie> cookies) {
		foreach (var cookie in cookies)
			_context.Http!.Cookies.Add(cookie);
	}

	public void InitAuthorization(string username, string password) {
		_context.Username = username;
		_context.Password = password;
	}

	public TokenResponse? Tokens => _context.Tokens;

	public async Task AuthenticateAsync() {
		if (_context.WellKnown is null)
			await Steps.LoadWellKnownConfigurationAsync(_context);

		var result = await Steps.SendAuthorizationRequestAsync(_context);

		if (result == StepResult.LoginPage)
			result = await Steps.LoginWithUsernameAndPasswordAsync(_context);

		if (result == StepResult.RedirectBackToClient)
			result = await Steps.ExchangeCodeForTokensAsync(_context);
	}

}