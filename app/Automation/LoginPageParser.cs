using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace Automation;

internal sealed record LoginFormField(string Name, string Value);

internal sealed record ParsedLoginPage(
	string ActionUrl,
	string Method,
	List<LoginFormField> HiddenFields,
	string? UsernameFieldName,
	string PasswordFieldName
);

internal enum LoginPageParseOutcome {
	LoginForm,
	CaptchaRequired,
	WebAuthnRequired,
	JavaScriptRequired,
	MfaRequired,
	NoFormFound,
}

internal sealed record LoginPageParseResult(
	LoginPageParseOutcome Outcome,
	ParsedLoginPage? Form = null,
	string? Detail = null
);

/// <summary>
/// Parses a login page's HTML to locate the login form, its hidden/anti-forgery fields, and
/// the username/password field names - and detects runtime evidence of MFA/CAPTCHA/WebAuthn/
/// JS-required pages that put the flow out of this engine's v1 scope.
/// </summary>
internal static class LoginPageParser {

	static readonly string[] MfaFieldHints = ["otp", "mfa", "verificationcode", "securitycode", "authenticator", "totp", "2fa"];
	static readonly string[] UsernameFieldHints = ["email", "user", "login", "signin", "logonidentifier"];

	public static async Task<LoginPageParseResult> ParseAsync(string html, string pageUrl) {
		var context = BrowsingContext.New(Configuration.Default);
		var document = await context.OpenAsync(request => request.Content(html).Address(pageUrl)).ConfigureAwait(false);

		if (document.QuerySelector(".g-recaptcha, .h-captcha, .cf-turnstile, iframe[src*='recaptcha'], iframe[src*='hcaptcha']") is not null)
			return new LoginPageParseResult(LoginPageParseOutcome.CaptchaRequired, Detail: "CAPTCHA widget detected on login page.");

		if (document.QuerySelectorAll("script").Any(ContainsWebAuthnMarkers))
			return new LoginPageParseResult(LoginPageParseOutcome.WebAuthnRequired, Detail: "WebAuthn/FIDO2 script markers detected on login page.");

		var forms = document.QuerySelectorAll("form").OfType<IHtmlFormElement>().ToList();
		var passwordInput = document.QuerySelectorAll("input").OfType<IHtmlInputElement>()
			.FirstOrDefault(i => string.Equals(i.Type, "password", StringComparison.OrdinalIgnoreCase));

		if (forms.Count == 0 || passwordInput is null) {
			if (LooksLikeJavaScriptShell(document))
				return new LoginPageParseResult(LoginPageParseOutcome.JavaScriptRequired, Detail: "No login form found; page looks like a JavaScript application shell.");

			return new LoginPageParseResult(LoginPageParseOutcome.NoFormFound, Detail: "No form with a password field was found on the page.");
		}

		var form = passwordInput.Closest("form") as IHtmlFormElement ?? forms[0];
		var inputs = form.QuerySelectorAll("input").OfType<IHtmlInputElement>().ToList();

		var usernameInput = ResolveUsernameField(inputs, passwordInput);

		var extraRequiredFields = inputs
			.Where(i => i != passwordInput && i != usernameInput)
			.Where(i => !IsStructuralInputType(i.Type))
			.Where(i => i.IsRequired)
			.Where(LooksLikeMfaField)
			.ToList();

		if (extraRequiredFields.Count > 0) {
			var fieldNames = extraRequiredFields.Select(f => f.Name ?? f.Id ?? "unnamed");
			return new LoginPageParseResult(LoginPageParseOutcome.MfaRequired, Detail: $"Login form requires additional field(s) suggesting MFA: {string.Join(", ", fieldNames)}");
		}

		var hiddenFields = inputs
			.Where(i => string.Equals(i.Type, "hidden", StringComparison.OrdinalIgnoreCase))
			.Where(i => !string.IsNullOrWhiteSpace(i.Name))
			.Select(i => new LoginFormField(i.Name!, i.GetAttribute("value") ?? string.Empty))
			.ToList();

		string actionUrl = ResolveActionUrl(form.GetAttribute("action"), document.Url ?? pageUrl);
		string method = (form.GetAttribute("method") ?? "POST").ToUpperInvariant();

		var parsed = new ParsedLoginPage(actionUrl, method, hiddenFields, usernameInput?.Name, passwordInput.Name!);
		return new LoginPageParseResult(LoginPageParseOutcome.LoginForm, parsed);
	}

	static IHtmlInputElement? ResolveUsernameField(List<IHtmlInputElement> inputs, IHtmlInputElement passwordInput) {
		var candidates = inputs
			.Where(i => i != passwordInput)
			.Where(i => !IsStructuralInputType(i.Type))
			.ToList();

		var byEmailType = candidates.FirstOrDefault(i => string.Equals(i.Type, "email", StringComparison.OrdinalIgnoreCase));
		if (byEmailType is not null)
			return byEmailType;

		var byHint = candidates.FirstOrDefault(i => MatchesAnyHint(i, UsernameFieldHints));
		if (byHint is not null)
			return byHint;

		return candidates.FirstOrDefault(i => string.Equals(i.Type, "text", StringComparison.OrdinalIgnoreCase))
			?? candidates.FirstOrDefault();
	}

	static bool LooksLikeMfaField(IHtmlInputElement input) {
		return MatchesAnyHint(input, MfaFieldHints);
	}

	static bool MatchesAnyHint(IHtmlInputElement input, string[] hints) {
		string haystack = $"{input.Name} {input.Id} {input.GetAttribute("autocomplete")}";
		return hints.Any(hint => haystack.Contains(hint, StringComparison.OrdinalIgnoreCase));
	}

	static string ResolveActionUrl(string? rawAction, string pageUrl) {
		if (string.IsNullOrWhiteSpace(rawAction))
			return pageUrl;

		if (Uri.TryCreate(rawAction, UriKind.Absolute, out var absoluteAction))
			return absoluteAction.ToString();

		return Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, rawAction, out var resolved)
			? resolved.ToString()
			: rawAction;
	}

	static bool IsStructuralInputType(string? type) {
		return type is "hidden" or "submit" or "button" or "checkbox" or "radio" or "image" or "reset";
	}

	static bool ContainsWebAuthnMarkers(IElement script) {
		string text = script.TextContent ?? string.Empty;
		return text.Contains("navigator.credentials", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("webauthn", StringComparison.OrdinalIgnoreCase);
	}

	static bool LooksLikeJavaScriptShell(IDocument document) {
		if (document.QuerySelector("#app, #root, [data-reactroot]") is not null)
			return true;

		string bodyText = document.Body?.TextContent?.Trim() ?? string.Empty;
		if (bodyText.Contains("enable javascript", StringComparison.OrdinalIgnoreCase))
			return true;

		return bodyText.Length < 40;
	}
}
