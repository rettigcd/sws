using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace Minimal;

internal static class Steps {

	// This class contains methods of the form:
	// Task<StepResult> DoSomethingAsync( Context ctx );

	/// <returns>
	/// <see cref="StepResult.WellKnownConfigRetrieved"/> if the endpoint returned a parseable document, populating <see cref="Context.WellKnown"/>.
	/// </returns>
	public static async Task<StepResult> LoadWellKnownConfigurationAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var wellKnownEndpoint = ctx.WellKnownEndpoint ?? throw new InvalidOperationException("Context.WellKnownEndpoint is not set.");

		var request = new HttpRequestMessage(HttpMethod.Get, wellKnownEndpoint);
		var response = await http.SendAsync(request);

		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Well-known endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		var json = await response.Content.ReadAsStringAsync();
		var config = JsonSerializer.Deserialize<WellKnownConfiguration>(json)
			?? throw new InvalidOperationException("Well-known endpoint response could not be deserialized.");

		ctx.WellKnown = config;
		return StepResult.WellKnownConfigRetrieved;
	}

	/// <returns>
	/// <see cref="StepResult.LoginPage"/> if the response is an HTML login page.
	/// <see cref="StepResult.RedirectBackToClient"/> if the response carries an authorization code back to
	/// redirect_uri, via redirect query, redirect fragment, or form_post - populating <see cref="Context.Code"/>.
	/// </returns>
	public static async Task<StepResult> SendAuthorizationRequestAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var authorizationEndpoint = ctx.WellKnown?.AuthorizationEndpoint
			?? throw new InvalidOperationException("WellKnown.AuthorizationEndpoint is not available. Call LoadWellKnownConfigurationAsync first.");

		var requestUri = BuildAuthorizeRequestUri(ctx, authorizationEndpoint);
		var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
		var response = await http.SendAsync(request);

		return await HandleAuthorizationResponseAsync(ctx, response, requestUri);
	}

	/// <summary>
	/// Shared by SendAuthorizationRequestAsync and LoginWithUsernameAndPasswordAsync - both send a
	/// request that can come back as a redirect-with-code, a form_post-with-code, or (only expected
	/// from the first request) a login page. requestUri is passed explicitly (rather than read off
	/// response.RequestMessage) since that's not guaranteed to be populated by every IAuthHttpClient.
	/// </summary>
	static async Task<StepResult> HandleAuthorizationResponseAsync(Context ctx, HttpResponseMessage response, Uri requestUri) {
		if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null) {
			var location = response.Headers.Location;
			CaptureAuthorizationResult(ctx, name => ExtractFromQueryOrFragment(location, name));
			return StepResult.RedirectBackToClient;
		}

		if (IsHtmlResponse(response)) {
			var html = await response.Content.ReadAsStringAsync();
			var pageUrl = requestUri.ToString();
			var browsingContext = BrowsingContext.New(Configuration.Default);
			var document = await browsingContext.OpenAsync(req => req.Content(html).Address(pageUrl)).ConfigureAwait(false);

			// form_post response_mode: the IdP returns an auto-submitting form carrying code/state as
			// hidden inputs, rather than a redirect. A hidden "code" input is what distinguishes this
			// from an actual login page.
			if (document.QuerySelector("input[name='code' i]") is not null) {
				CaptureAuthorizationResult(ctx, name => document.QuerySelector($"input[name='{name}' i]")?.GetAttribute("value"));
				return StepResult.RedirectBackToClient;
			}

			ctx.LoginPageHtml = html;
			ctx.LoginPageUrl = pageUrl;
			return StepResult.LoginPage;
		}

		throw new InvalidOperationException($"Unexpected response from authorization endpoint: {(int)response.StatusCode} {response.StatusCode}.");
	}

	/// <summary>
	/// Builds the AuthCode+PKCE authorize request: response_type=code plus the caller-supplied
	/// client_id/redirect_uri/scope, and freshly-generated state/code_verifier/code_challenge
	/// (stored on ctx - state is checked against the callback later; code_verifier is needed
	/// unmodified at token exchange).
	/// </summary>
	static Uri BuildAuthorizeRequestUri(Context ctx, string authorizationEndpoint) {
		var clientId = ctx.ClientId ?? throw new InvalidOperationException("Context.ClientId is not set.");
		var redirectUri = ctx.RedirectUri ?? throw new InvalidOperationException("Context.RedirectUri is not set.");
		var scope = ctx.Scope ?? throw new InvalidOperationException("Context.Scope is not set.");

		ctx.State = Crypto.GenerateState();
		ctx.CodeVerifier = Crypto.GenerateCodeVerifier();
		ctx.CodeChallenge = Crypto.DeriveCodeChallengeS256(ctx.CodeVerifier);

		var query = string.Join('&', [
			"response_type=code",
			$"client_id={Uri.EscapeDataString(clientId)}",
			$"redirect_uri={Uri.EscapeDataString(redirectUri)}",
			$"scope={Uri.EscapeDataString(scope)}",
			$"state={Uri.EscapeDataString(ctx.State)}",
			$"code_challenge={Uri.EscapeDataString(ctx.CodeChallenge)}",
			"code_challenge_method=S256",
		]);

		return new Uri($"{authorizationEndpoint}?{query}");
	}

	/// <summary>
	/// Reads code/state/error via the supplied lookup (works the same whether the values came from a
	/// redirect URI or from form_post hidden inputs), validates them, and stores the code on ctx.
	/// </summary>
	static void CaptureAuthorizationResult(Context ctx, Func<string, string?> lookup) {
		var error = lookup("error");
		if (error is not null) {
			var errorDescription = lookup("error_description") ?? error;
			throw new InvalidOperationException($"Authorization server returned error: {errorDescription}");
		}

		var code = lookup("code");
		if (string.IsNullOrEmpty(code))
			throw new InvalidOperationException("Authorization response did not include a code.");

		var returnedState = lookup("state");
		if (!string.IsNullOrEmpty(ctx.State) && !string.IsNullOrEmpty(returnedState) && !string.Equals(returnedState, ctx.State, StringComparison.Ordinal))
			throw new InvalidOperationException("Returned state did not match the state sent on the authorization request.");

		ctx.Code = code;
	}

	/// <summary>Checks the query string first (response_mode=query, the common case), then the fragment (response_mode=fragment).</summary>
	static string? ExtractFromQueryOrFragment(Uri uri, string name) {
		return ParseFormEncoded(uri.Query.TrimStart('?')).GetValueOrDefault(name)
			?? ParseFormEncoded(uri.Fragment.TrimStart('#')).GetValueOrDefault(name);
	}

	static Dictionary<string, string> ParseFormEncoded(string raw) {
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var piece in raw.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = piece.Split('=', 2);
			var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
			if (!string.IsNullOrWhiteSpace(key))
				result[key] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
		}

		return result;
	}

	static readonly string[] UsernameFieldHints = ["email", "user", "login", "signin", "logonidentifier"];

	/// <returns>
	/// <see cref="StepResult.RedirectBackToClient"/> once the submitted credentials produce an authorization code.
	/// </returns>
	public static async Task<StepResult> LoginWithUsernameAndPasswordAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var username = ctx.Username ?? throw new InvalidOperationException("Context.Username is not set.");
		var password = ctx.Password ?? throw new InvalidOperationException("Context.Password is not set.");
		var html = ctx.LoginPageHtml ?? throw new InvalidOperationException("Context.LoginPageHtml is not set. Call SendAuthorizationRequestAsync first and ensure it returned StepResult.LoginPage.");
		var pageUrl = ctx.LoginPageUrl ?? throw new InvalidOperationException("Context.LoginPageUrl is not set. Call SendAuthorizationRequestAsync first and ensure it returned StepResult.LoginPage.");

		var browsingContext = BrowsingContext.New(Configuration.Default);
		var document = await browsingContext.OpenAsync(req => req.Content(html).Address(pageUrl)).ConfigureAwait(false);

		var passwordInput = document.QuerySelectorAll("input").OfType<IHtmlInputElement>()
			.FirstOrDefault(i => string.Equals(i.Type, "password", StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidOperationException("Login page does not contain a password field.");

		var form = passwordInput.Closest("form") as IHtmlFormElement
			?? throw new InvalidOperationException("Login page's password field is not inside a form.");

		var formInputs = form.QuerySelectorAll("input").OfType<IHtmlInputElement>().ToList();
		var usernameInput = ResolveUsernameField(formInputs, passwordInput)
			?? throw new InvalidOperationException("Login page does not contain a recognizable username field.");

		var formData = formInputs
			.Where(i => string.Equals(i.Type, "hidden", StringComparison.OrdinalIgnoreCase))
			.Where(i => !string.IsNullOrWhiteSpace(i.Name))
			.ToDictionary(i => i.Name!, i => i.GetAttribute("value") ?? "");

		formData[usernameInput.Name ?? throw new InvalidOperationException("Username field has no name attribute.")] = username;
		formData[passwordInput.Name ?? throw new InvalidOperationException("Password field has no name attribute.")] = password;

		var actionUrl = ResolveActionUrl(form.GetAttribute("action"), pageUrl);
		var request = new HttpRequestMessage(HttpMethod.Post, actionUrl) { Content = new FormUrlEncodedContent(formData) };
		var response = await http.SendAsync(request);

		var result = await HandleAuthorizationResponseAsync(ctx, response, actionUrl);
		if (result != StepResult.RedirectBackToClient)
			throw new InvalidOperationException("Login form submission did not complete authentication (invalid credentials, MFA required, or another login page was returned).");

		return result;
	}

	static IHtmlInputElement? ResolveUsernameField(List<IHtmlInputElement> inputs, IHtmlInputElement passwordInput) {
		var candidates = inputs
			.Where(i => i != passwordInput)
			.Where(i => i.Type is not ("hidden" or "submit" or "button" or "checkbox" or "radio" or "image" or "reset"))
			.ToList();

		return candidates.FirstOrDefault(i => string.Equals(i.Type, "email", StringComparison.OrdinalIgnoreCase))
			?? candidates.FirstOrDefault(i => UsernameFieldHints.Any(hint => $"{i.Name} {i.Id} {i.GetAttribute("autocomplete")}".Contains(hint, StringComparison.OrdinalIgnoreCase)))
			?? candidates.FirstOrDefault(i => string.Equals(i.Type, "text", StringComparison.OrdinalIgnoreCase))
			?? candidates.FirstOrDefault();
	}

	static Uri ResolveActionUrl(string? rawAction, string pageUrl) {
		if (!string.IsNullOrWhiteSpace(rawAction) && Uri.TryCreate(rawAction, UriKind.Absolute, out var absoluteAction))
			return absoluteAction;

		var baseUri = new Uri(pageUrl);
		return string.IsNullOrWhiteSpace(rawAction) ? baseUri : new Uri(baseUri, rawAction);
	}

	/// <returns>
	/// <see cref="StepResult.TokensReceived"/> once the token endpoint returns an access token, populating <see cref="Context.Tokens"/>.
	/// </returns>
	public static async Task<StepResult> ExchangeCodeForTokensAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var tokenEndpoint = ctx.WellKnown?.TokenEndpoint
			?? throw new InvalidOperationException("WellKnown.TokenEndpoint is not available. Call LoadWellKnownConfigurationAsync first.");
		var code = ctx.Code ?? throw new InvalidOperationException("Context.Code is not set. Call SendAuthorizationRequestAsync first.");
		var codeVerifier = ctx.CodeVerifier ?? throw new InvalidOperationException("Context.CodeVerifier is not set. Call SendAuthorizationRequestAsync first.");
		var clientId = ctx.ClientId ?? throw new InvalidOperationException("Context.ClientId is not set.");
		var redirectUri = ctx.RedirectUri ?? throw new InvalidOperationException("Context.RedirectUri is not set.");

		var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) {
			Content = new FormUrlEncodedContent(new Dictionary<string, string> {
				["grant_type"] = "authorization_code",
				["code"] = code,
				["redirect_uri"] = redirectUri,
				["client_id"] = clientId,
				["code_verifier"] = codeVerifier,
			}),
		};

		var response = await http.SendAsync(request);
		var json = await response.Content.ReadAsStringAsync();

		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Token endpoint returned {(int)response.StatusCode} {response.StatusCode}: {ExtractTokenErrorMessage(json)}");

		var tokens = JsonSerializer.Deserialize<TokenResponse>(json)
			?? throw new InvalidOperationException("Token endpoint response could not be deserialized.");

		if (string.IsNullOrEmpty(tokens.AccessToken))
			throw new InvalidOperationException("Token endpoint response did not include an access_token.");

		ctx.Tokens = tokens;
		return StepResult.TokensReceived;
	}

	static string ExtractTokenErrorMessage(string json) {
		try {
			using var document = JsonDocument.Parse(json);
			if (document.RootElement.TryGetProperty("error_description", out var description) && description.ValueKind == JsonValueKind.String)
				return description.GetString()!;

			if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
				return error.GetString()!;
		}
		catch (JsonException) {
			// fall through
		}

		return "(no error details in response body)";
	}

	static bool IsHtmlResponse(HttpResponseMessage response) {
		return response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
	}

}