namespace sws.Tests;

using Shouldly;
using Xunit;

public class SourceReportBuilder_Tests {
	[Fact]
	public void BuildSessionSourcesReport_UsesAzureB2CSourceReference_ForB2CFlowValues() {
		var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var challenge = "xqztrplkmnsvwbcdfghjklmn123456";

		var b2cSession = BuildSession(
			sessionId: 101,
			url: $"https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signup_signin/oauth2/v2.0/authorize?client_id=app-client&code_challenge={challenge}&response_type=code",
			requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		);

		var report = SourceReportBuilder.BuildSessionSourcesReport(
			sessionIndex: 0,
			sessions: [b2cSession],
			missing: missing,
			unsourcedCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		);

		var replacement = report.RequestPlan.Replacements.Values.FirstOrDefault(r => r.Placeholder.StartsWith("{code_challenge", StringComparison.OrdinalIgnoreCase));
		replacement.ShouldNotBeNull();
		replacement.Source.ShouldBe("{AzureB2C:code_challenge}");
		missing.ContainsKey(replacement.Placeholder).ShouldBeFalse();
	}

	[Fact]
	public void BuildSessionSourcesReport_FlagsUnsourcedRequestCookies() {
		var sourcedCookieValue = "abc123";
		var previousSession = BuildSession(
			sessionId: 1,
			url: "https://example.com/start",
			requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["Set-Cookie"] = $"session={sourcedCookieValue}; Path=/; Secure",
			}
		);

		var targetSession = BuildSession(
			sessionId: 2,
			url: "https://example.com/next",
			requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["session"] = sourcedCookieValue,
				["missing-cookie"] = "not-found",
			},
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		);

		var unsourcedCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var report = SourceReportBuilder.BuildSessionSourcesReport(
			sessionIndex: 1,
			sessions: [previousSession, targetSession],
			missing: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			unsourcedCookies: unsourcedCookies
		);

		unsourcedCookies.Count.ShouldBe(1);
		unsourcedCookies.ContainsKey("missing-cookie").ShouldBeTrue();
		unsourcedCookies["missing-cookie"].ShouldBe("not-found");
		report.RequestPlan.Cookies.Count.ShouldBe(1);
		report.RequestPlan.Cookies.ContainsKey("missing-cookie").ShouldBeTrue();
		report.RequestPlan.Cookies["missing-cookie"].ShouldBe("not-found");
		report.RequestPlan.Headers.ContainsKey("Cookie").ShouldBeFalse();
	}

	[Fact]
	public void BuildSessionSourcesReport_DoesNotFlagAzureB2CCookiesAsUnsourced() {
		var b2cSession = BuildSession(
			sessionId: 3,
			url: "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signup_signin/oauth2/v2.0/authorize?client_id=abc&response_type=code",
			requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["x-ms-cpim-csrf"] = "csrf-token",
			},
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		);

		var unsourcedCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var report = SourceReportBuilder.BuildSessionSourcesReport(
			sessionIndex: 0,
			sessions: [b2cSession],
			missing: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			unsourcedCookies: unsourcedCookies
		);

		unsourcedCookies.Count.ShouldBe(0);
		report.RequestPlan.Cookies.Count.ShouldBe(0);
	}

	static Session BuildSession(
		int sessionId,
		string url,
		Dictionary<string, string> requestCookies,
		Dictionary<string, string> responseHeaders
	) {
		var uri = new Uri(url);
		var queryParameters = ParseQueryParameters(uri);

		var request = new Request(
			$"GET {url} HTTP/1.1",
			"GET",
			url,
			"HTTP/1.1",
			url,
			uri.Host,
			queryParameters,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			requestCookies,
			new List<string>(),
			new Body(0, null, "none", new List<string>()),
			null,
			null,
			new List<string>()
		);

		var response = new Response(
			"HTTP/1.1 200 OK",
			200,
			"OK",
			responseHeaders,
			new Body(0, null, "none", new List<string>()),
			null,
			null,
			new List<string>()
		);

		return new Session(sessionId, null, request, response);
	}

	static Dictionary<string, string> ParseQueryParameters(Uri uri) {
		var query = uri.Query;
		var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(query) || query == "?")
			return parameters;

		foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			var split = pair.Split('=', 2);
			var key = Uri.UnescapeDataString(split[0]);
			var value = split.Length > 1 ? Uri.UnescapeDataString(split[1]) : string.Empty;
			if (!string.IsNullOrWhiteSpace(key))
				parameters[key] = value;
		}

		return parameters;
	}
}