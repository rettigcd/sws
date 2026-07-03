namespace sws.Tests;

using Automation;
using Shouldly;
using Xunit;

public class LoginPageParser_Tests {

	[Fact]
	public async Task ParseAsync_ExtractsHiddenFieldsAndFieldNames_FromSimpleLoginForm() {
		var html = """
			<html><body>
			<form method="POST" action="/login/submit">
				<input type="hidden" name="__RequestVerificationToken" value="csrf-abc-123" />
				<input type="hidden" name="StateProperties" value="state-blob" />
				<input type="email" name="Username" />
				<input type="password" name="Password" />
				<button type="submit">Sign in</button>
			</form>
			</body></html>
			""";

		var result = await LoginPageParser.ParseAsync(html, "https://tenant.b2clogin.com/login/page");

		result.Outcome.ShouldBe(LoginPageParseOutcome.LoginForm);
		result.Form.ShouldNotBeNull();
		result.Form!.ActionUrl.ShouldBe("https://tenant.b2clogin.com/login/submit");
		result.Form.Method.ShouldBe("POST");
		result.Form.UsernameFieldName.ShouldBe("Username");
		result.Form.PasswordFieldName.ShouldBe("Password");
		result.Form.HiddenFields.ShouldContain(f => f.Name == "__RequestVerificationToken" && f.Value == "csrf-abc-123");
		result.Form.HiddenFields.ShouldContain(f => f.Name == "StateProperties" && f.Value == "state-blob");
	}

	[Fact]
	public async Task ParseAsync_ResolvesRelativeFormAction_AgainstPageUrl() {
		var html = """<html><body><form action="submit?step=2"><input type="password" name="pw" /></form></body></html>""";

		var result = await LoginPageParser.ParseAsync(html, "https://tenant.b2clogin.com/policy/login/");

		result.Form!.ActionUrl.ShouldBe("https://tenant.b2clogin.com/policy/login/submit?step=2");
	}

	[Fact]
	public async Task ParseAsync_ResolvesUsernameField_ByHintWhenNoEmailType() {
		var html = """
			<html><body>
			<form>
				<input type="text" name="logonIdentifier" />
				<input type="password" name="password" />
			</form>
			</body></html>
			""";

		var result = await LoginPageParser.ParseAsync(html, "https://tenant.b2clogin.com/login");

		result.Form!.UsernameFieldName.ShouldBe("logonIdentifier");
	}

	[Fact]
	public async Task ParseAsync_DetectsCaptcha() {
		var html = """<html><body><form><div class="g-recaptcha"></div><input type="password" name="pw" /></form></body></html>""";

		var result = await LoginPageParser.ParseAsync(html, "https://tenant.b2clogin.com/login");

		result.Outcome.ShouldBe(LoginPageParseOutcome.CaptchaRequired);
	}

	[Fact]
	public async Task ParseAsync_DetectsWebAuthnMarkers() {
		var html = """<html><body><script>if (navigator.credentials) { doWebAuthn(); }</script></body></html>""";

		var result = await LoginPageParser.ParseAsync(html, "https://tenant.b2clogin.com/login");

		result.Outcome.ShouldBe(LoginPageParseOutcome.WebAuthnRequired);
	}

	[Fact]
	public async Task ParseAsync_DetectsJavaScriptShell_WhenNoFormAndAppRootPresent() {
		var html = """<html><body><div id="root"></div></body></html>""";

		var result = await LoginPageParser.ParseAsync(html, "https://app.example.com/login");

		result.Outcome.ShouldBe(LoginPageParseOutcome.JavaScriptRequired);
	}

	[Fact]
	public async Task ParseAsync_DetectsMfaRequiredField() {
		var html = """
			<html><body>
			<form>
				<input type="email" name="Username" />
				<input type="password" name="Password" />
				<input type="text" name="otpCode" required />
			</form>
			</body></html>
			""";

		var result = await LoginPageParser.ParseAsync(html, "https://tenant.b2clogin.com/login");

		result.Outcome.ShouldBe(LoginPageParseOutcome.MfaRequired);
	}

	[Fact]
	public async Task ParseAsync_ReturnsNoFormFound_WhenNoPasswordFieldPresent() {
		var html = """<html><body><p>This is an ordinary informational page with no login form and no JavaScript app shell markers present anywhere.</p></body></html>""";

		var result = await LoginPageParser.ParseAsync(html, "https://app.example.com/somewhere");

		result.Outcome.ShouldBe(LoginPageParseOutcome.NoFormFound);
	}
}
