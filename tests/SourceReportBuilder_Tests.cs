namespace sws.Tests;

using Shouldly;
using Xunit;

public class SourceReportBuilder_Tests {
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

	static Session BuildSession(
		int sessionId,
		string url,
		Dictionary<string, string> requestCookies,
		Dictionary<string, string> responseHeaders
	) {
		var request = new Request(
			$"GET {url} HTTP/1.1",
			"GET",
			url,
			"HTTP/1.1",
			url,
			new Uri(url).Host,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
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
}