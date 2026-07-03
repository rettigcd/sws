namespace sws.Tests;

using Minimal;
using Shouldly;
using Xunit;

public class Steps_LoginWithUsernameAndPasswordAsync_Tests {

	const string LoginPageUrl = "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/self-asserted";
	const string RedirectUri = "https://app.example.com/callback";

	static Context BuildContext(FakeAuthHttpClient http, string html, string username = "user@example.com", string password = "hunter2") {
		return new Context {
			Http = http,
			Username = username,
			Password = password,
			LoginPageHtml = html,
			LoginPageUrl = LoginPageUrl,
		};
	}

	static string ExtractFormValue(string formEncodedBody, string name) {
		var pairs = formEncodedBody.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
		return pairs.GetValueOrDefault(name, "");
	}

	[Fact]
	public async Task LoginWithUsernameAndPasswordAsync_PostsHiddenFieldsAndCredentials_ToAbsoluteFormAction() {
		const string submitUrl = "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signin/self-asserted/submit";
		var html = $"""
			<html><body>
			<form method="POST" action="{submitUrl}">
				<input type="hidden" name="__RequestVerificationToken" value="csrf-token-xyz" />
				<input type="email" name="Username" />
				<input type="password" name="Password" />
			</form>
			</body></html>
			""";

		var fakeHttpClient = new FakeAuthHttpClient();
		string? postedBody = null;
		fakeHttpClient.Enqueue(HttpMethod.Post, submitUrl, request => {
			postedBody = request.Content!.ReadAsStringAsync().Result;
			return FakeResponses.Redirect($"{RedirectUri}?code=login-code&state=");
		});

		var ctx = BuildContext(fakeHttpClient, html);

		var result = await Steps.LoginWithUsernameAndPasswordAsync(ctx);

		result.ShouldBe(StepResult.RedirectBackToClient);
		ctx.Code.ShouldBe("login-code");

		postedBody.ShouldNotBeNull();
		ExtractFormValue(postedBody, "Username").ShouldBe("user@example.com");
		ExtractFormValue(postedBody, "Password").ShouldBe("hunter2");
		ExtractFormValue(postedBody, "__RequestVerificationToken").ShouldBe("csrf-token-xyz");
	}

	[Fact]
	public async Task LoginWithUsernameAndPasswordAsync_ResolvesRelativeFormAction_AgainstLoginPageUrl() {
		var html = """
			<html><body>
			<form method="POST" action="submit">
				<input type="email" name="Username" />
				<input type="password" name="Password" />
			</form>
			</body></html>
			""";

		var fakeHttpClient = new FakeAuthHttpClient();
		var expectedActionUrl = new Uri(new Uri(LoginPageUrl), "submit").ToString();
		fakeHttpClient.Enqueue(HttpMethod.Post, expectedActionUrl, _ => FakeResponses.Redirect($"{RedirectUri}?code=relative-action-code&state="));

		var ctx = BuildContext(fakeHttpClient, html);

		var result = await Steps.LoginWithUsernameAndPasswordAsync(ctx);

		result.ShouldBe(StepResult.RedirectBackToClient);
		ctx.Code.ShouldBe("relative-action-code");
	}

	[Fact]
	public async Task LoginWithUsernameAndPasswordAsync_ResolvesUsernameField_ByHint_WhenNoEmailTypeInputExists() {
		var html = """
			<html><body>
			<form method="POST" action="https://tenant.b2clogin.com/submit">
				<input type="text" name="logonIdentifier" />
				<input type="password" name="Password" />
			</form>
			</body></html>
			""";

		var fakeHttpClient = new FakeAuthHttpClient();
		string? postedBody = null;
		fakeHttpClient.Enqueue(HttpMethod.Post, "https://tenant.b2clogin.com/submit", request => {
			postedBody = request.Content!.ReadAsStringAsync().Result;
			return FakeResponses.Redirect($"{RedirectUri}?code=hint-code&state=");
		});

		var ctx = BuildContext(fakeHttpClient, html);

		var result = await Steps.LoginWithUsernameAndPasswordAsync(ctx);

		result.ShouldBe(StepResult.RedirectBackToClient);
		ExtractFormValue(postedBody!, "logonIdentifier").ShouldBe("user@example.com");
	}

	[Fact]
	public async Task LoginWithUsernameAndPasswordAsync_CapturesCode_FromFormPostResponseAfterSubmission() {
		const string submitUrl = "https://tenant.b2clogin.com/submit";
		var html = $"""
			<html><body>
			<form method="POST" action="{submitUrl}">
				<input type="email" name="Username" />
				<input type="password" name="Password" />
			</form>
			</body></html>
			""";

		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Post, submitUrl, _ => FakeResponses.Html("""
			<html><body>
			<form method="POST" action="https://app.example.com/callback">
				<input type="hidden" name="code" value="form-post-login-code" />
			</form>
			</body></html>
			"""));

		var ctx = BuildContext(fakeHttpClient, html);

		var result = await Steps.LoginWithUsernameAndPasswordAsync(ctx);

		result.ShouldBe(StepResult.RedirectBackToClient);
		ctx.Code.ShouldBe("form-post-login-code");
	}

	[Fact]
	public async Task LoginWithUsernameAndPasswordAsync_Throws_WhenLoginPageIsReDisplayedAfterSubmission() {
		const string submitUrl = "https://tenant.b2clogin.com/submit";
		var html = $"""
			<html><body>
			<form method="POST" action="{submitUrl}">
				<input type="email" name="Username" />
				<input type="password" name="Password" />
			</form>
			</body></html>
			""";

		var fakeHttpClient = new FakeAuthHttpClient();
		fakeHttpClient.Enqueue(HttpMethod.Post, submitUrl, _ => FakeResponses.Html(html));

		var ctx = BuildContext(fakeHttpClient, html);

		var ex = await Should.ThrowAsync<InvalidOperationException>(() => Steps.LoginWithUsernameAndPasswordAsync(ctx));
		ex.Message.ShouldContain("did not complete authentication");
	}

	[Fact]
	public async Task LoginWithUsernameAndPasswordAsync_Throws_WhenPageHasNoPasswordField() {
		var html = """
			<html><body>
			<form method="POST" action="https://tenant.b2clogin.com/submit">
				<input type="email" name="Username" />
			</form>
			</body></html>
			""";

		var fakeHttpClient = new FakeAuthHttpClient();
		var ctx = BuildContext(fakeHttpClient, html);

		var ex = await Should.ThrowAsync<InvalidOperationException>(() => Steps.LoginWithUsernameAndPasswordAsync(ctx));
		ex.Message.ShouldContain("password field");
		fakeHttpClient.SentRequests.ShouldBeEmpty();
	}

	[Fact]
	public async Task LoginWithUsernameAndPasswordAsync_Throws_WhenNoRecognizableUsernameField() {
		var html = """
			<html><body>
			<form method="POST" action="https://tenant.b2clogin.com/submit">
				<input type="password" name="Password" />
			</form>
			</body></html>
			""";

		var fakeHttpClient = new FakeAuthHttpClient();
		var ctx = BuildContext(fakeHttpClient, html);

		var ex = await Should.ThrowAsync<InvalidOperationException>(() => Steps.LoginWithUsernameAndPasswordAsync(ctx));
		ex.Message.ShouldContain("username field");
		fakeHttpClient.SentRequests.ShouldBeEmpty();
	}

	[Theory]
	[InlineData(null, "hunter2", "html", LoginPageUrl, "Context.Username is not set.")]
	[InlineData("user@example.com", null, "html", LoginPageUrl, "Context.Password is not set.")]
	public async Task LoginWithUsernameAndPasswordAsync_Throws_WhenCredentialsAreMissing(
		string? username, string? password, string html, string loginPageUrl, string expectedMessage
	) {
		var fakeHttpClient = new FakeAuthHttpClient();
		var ctx = new Context {
			Http = fakeHttpClient,
			Username = username,
			Password = password,
			LoginPageHtml = html,
			LoginPageUrl = loginPageUrl,
		};

		var ex = await Should.ThrowAsync<InvalidOperationException>(() => Steps.LoginWithUsernameAndPasswordAsync(ctx));
		ex.Message.ShouldBe(expectedMessage);
		fakeHttpClient.SentRequests.ShouldBeEmpty();
	}

	[Fact]
	public async Task LoginWithUsernameAndPasswordAsync_Throws_WhenLoginPageHtmlIsMissing() {
		var fakeHttpClient = new FakeAuthHttpClient();
		var ctx = new Context {
			Http = fakeHttpClient,
			Username = "user@example.com",
			Password = "hunter2",
			LoginPageUrl = LoginPageUrl,
		};

		var ex = await Should.ThrowAsync<InvalidOperationException>(() => Steps.LoginWithUsernameAndPasswordAsync(ctx));
		ex.Message.ShouldContain("Context.LoginPageHtml is not set");
		fakeHttpClient.SentRequests.ShouldBeEmpty();
	}
}
