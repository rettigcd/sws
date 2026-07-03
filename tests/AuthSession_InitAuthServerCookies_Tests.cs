namespace sws.Tests;

using System.Net;
using Minimal;
using Shouldly;
using Xunit;

public class AuthSession_InitAuthServerCookies_Tests {

	[Fact]
	public void InitAuthServerCookies_AddsCookie_ToUnderlyingHttpClientCookieContainer() {
		var fakeHttpClient = new FakeAuthHttpClient();
		var session = new AuthSession(fakeHttpClient);

		session.InitAuthServerCookies([new Cookie("x-ms-cpim-sso", "sso-cookie-value", "/", "tenant.b2clogin.com")]);

		var cookies = fakeHttpClient.Cookies.GetCookies(new Uri("https://tenant.b2clogin.com/"));
		cookies.Count.ShouldBe(1);
		cookies[0]!.Name.ShouldBe("x-ms-cpim-sso");
		cookies[0]!.Value.ShouldBe("sso-cookie-value");
	}

	[Fact]
	public void InitAuthServerCookies_AddsMultipleCookies() {
		var fakeHttpClient = new FakeAuthHttpClient();
		var session = new AuthSession(fakeHttpClient);

		session.InitAuthServerCookies([
			new Cookie("x-ms-cpim-sso", "sso-cookie-value", "/", "tenant.b2clogin.com"),
			new Cookie("x-ms-cpim-csrf", "csrf-cookie-value", "/", "tenant.b2clogin.com"),
		]);

		var cookies = fakeHttpClient.Cookies.GetCookies(new Uri("https://tenant.b2clogin.com/"));
		cookies.Count.ShouldBe(2);
		cookies.Cast<Cookie>().ShouldContain(c => c.Name == "x-ms-cpim-sso" && c.Value == "sso-cookie-value");
		cookies.Cast<Cookie>().ShouldContain(c => c.Name == "x-ms-cpim-csrf" && c.Value == "csrf-cookie-value");
	}

	[Fact]
	public void InitAuthServerCookies_DoesNotAffectCookiesForADifferentDomain() {
		var fakeHttpClient = new FakeAuthHttpClient();
		var session = new AuthSession(fakeHttpClient);

		session.InitAuthServerCookies([new Cookie("x-ms-cpim-sso", "sso-cookie-value", "/", "tenant.b2clogin.com")]);

		var cookies = fakeHttpClient.Cookies.GetCookies(new Uri("https://unrelated.example.com/"));
		cookies.Count.ShouldBe(0);
	}
}
