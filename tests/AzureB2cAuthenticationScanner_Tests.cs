namespace sws.Tests;

using System.Text.Json;
using Shouldly;
using Xunit;

public class AzureB2cAuthenticationScanner_Tests {
	[Fact]
	public void Scan_FindsAzureB2cFlow() {
		var sessions = new List<Session> {
			BuildSession(
				2,
				"GET",
				"https://tqlidentitystage.b2clogin.com/tqlidentitystage.onmicrosoft.com/b2c_1a_signup_signin_passwordreset/oauth2/v2.0/authorize?client_id=abc&code_challenge=xyz&nonce=123",
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
					["client_id"] = "abc",
					["code_challenge"] = "xyz",
					["nonce"] = "123",
				},
				formBody: null,
				responseJson: null
			),
			BuildSession(
				3,
				"POST",
				"https://tqlidentitystage.b2clogin.com/tqlidentitystage.onmicrosoft.com/b2c_1a_signup_signin_passwordreset/oauth2/v2.0/token",
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
				formBody: new List<FormBodyEntry> {
					new("grant_type", "authorization_code"),
					new("code_verifier", "some-code-verifier"),
				},
				responseJson: JsonDocument.Parse("""
					{
						"access_token": "abc",
						"refresh_token": "def",
						"expires_in": 3600
					}
				""").RootElement.Clone()
			)
		};

		var report = AzureB2cAuthenticationScanner.Scan(sessions);

		report.Flows.Count.ShouldBe(1);
		var flow = report.Flows[0];
		flow.Host.ShouldBe("tqlidentitystage.b2clogin.com");
		flow.Policy.ShouldBe("b2c_1a_signup_signin_passwordreset");
		flow.SessionIds.ShouldBe([2, 3]);
		flow.AuthorizeSessionIds.ShouldBe([2]);
		flow.TokenSessionIds.ShouldBe([3]);
		flow.Indicators.ShouldContain("endpoint:authorize");
		flow.Indicators.ShouldContain("endpoint:token");
		flow.Indicators.ShouldContain("response:tokens");
		flow.ConfidenceScore.ShouldBeGreaterThanOrEqualTo(80);
	}

	static Session BuildSession(
		int sessionId,
		string method,
		string url,
		Dictionary<string, string> query,
		List<FormBodyEntry>? formBody,
		JsonElement? responseJson
	) {
		var request = new Request(
			$"{method} {url} HTTP/1.1",
			method,
			url,
			"HTTP/1.1",
			url,
			new Uri(url).Host,
			query,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			new List<string>(),
			new Body(0, null, "none", new List<string>()),
			null,
			formBody,
			new List<string>()
		);

		var response = new Response(
			"HTTP/1.1 200 OK",
			200,
			"OK",
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			new Body(0, null, "none", new List<string>()),
			null,
			responseJson,
			new List<string>()
		);

		return new Session(sessionId, null, request, response);
	}
}