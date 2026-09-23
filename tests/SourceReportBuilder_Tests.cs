namespace sws.Tests;

using Analysis;
using Saz;
using System.Text.Json;
using Auth;
using Shouldly;
using Xunit;

public class SourceReportBuilder_Tests {
	[Fact]
	public void BuildExchangeSourcesReport_UsesAzureB2CSourceReference_ForB2CFlowValues() {
		var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var challenge = "xqztrplkmnsvwbcdfghjklmn123456";

		var b2cExchange = BuildExchange(
			exchangeId: 101,
			url: $"https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signup_signin/oauth2/v2.0/authorize?client_id=app-client&code_challenge={challenge}&response_type=code",
			requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		);

		var report = SourceReportBuilder.BuildExchangeSourcesReport(
			exchangeIndex: 0,
			exchanges: [b2cExchange],
			missing: missing,
			unsourcedCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		);

		var replacement = report.RequestPlan.Replacements.Values.FirstOrDefault(r => r.Placeholder.StartsWith("{code_challenge", StringComparison.OrdinalIgnoreCase));
		replacement.ShouldNotBeNull();
		replacement.Source.ShouldBe("{AzureB2C:code_challenge}");
		report.RequestType.ShouldBe(RequestType.AuthorizationRequest_AuthCodeWithPKCE);
		missing.ContainsKey(replacement.Placeholder).ShouldBeFalse();
	}

	[Fact]
	public void BuildExchangeSourcesReport_FlagsUnsourcedRequestCookies() {
		var sourcedCookieValue = "abc123";
		var previousExchange = BuildExchange(
			exchangeId: 1,
			url: "https://example.com/start",
			requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["Set-Cookie"] = $"session={sourcedCookieValue}; Path=/; Secure",
			}
		);

		var targetExchange = BuildExchange(
			exchangeId: 2,
			url: "https://example.com/next",
			requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["session"] = sourcedCookieValue,
				["missing-cookie"] = "not-found",
			},
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		);

		var unsourcedCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var report = SourceReportBuilder.BuildExchangeSourcesReport(
			exchangeIndex: 1,
			exchanges: [previousExchange, targetExchange],
			missing: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			unsourcedCookies: unsourcedCookies
		);

		unsourcedCookies.Count.ShouldBe(1);
		unsourcedCookies.ContainsKey("missing-cookie").ShouldBeTrue();
		unsourcedCookies["missing-cookie"].ShouldBe("not-found");
		report.RequestType.ShouldBe(RequestType.Unknown);
		report.RequestPlan.Cookies.Count.ShouldBe(1);
		report.RequestPlan.Cookies.ContainsKey("missing-cookie").ShouldBeTrue();
		report.RequestPlan.Cookies["missing-cookie"].ShouldBe("not-found");
		report.RequestPlan.Headers.ContainsKey("Cookie").ShouldBeFalse();
	}

	[Fact]
	public void BuildExchangeSourcesReport_DoesNotFlagAzureB2CCookiesAsUnsourced() {
		var b2cExchange = BuildExchange(
			exchangeId: 3,
			url: "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signup_signin/oauth2/v2.0/authorize?client_id=abc&response_type=code",
			requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				["x-ms-cpim-csrf"] = "csrf-token",
			},
			responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		);

		var unsourcedCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var report = SourceReportBuilder.BuildExchangeSourcesReport(
			exchangeIndex: 0,
			exchanges: [b2cExchange],
			missing: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			unsourcedCookies: unsourcedCookies
		);

		unsourcedCookies.Count.ShouldBe(0);
		report.RequestType.ShouldBe(RequestType.AuthorizationRequest_AuthCode);
		report.RequestPlan.Cookies.Count.ShouldBe(0);
	}

	[Fact]
	public void WriteAllExchangeSourcesReport_IncludesClassifierRequestTypeInSerializedMappings() {
		// Given: a tiny flow with authorize, callback, and token exchange exchanges.
		var tempDirectory = Path.Combine(Path.GetTempPath(), $"sws-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDirectory);
		var outputBasePath = Path.Combine(tempDirectory, "capture.exchanges.json");

		var exchanges = new List<Exchange> {
			BuildExchange(
				exchangeId: 10,
				url: "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signup_signin/oauth2/v2.0/authorize?client_id=abc&response_type=code&code_challenge=challenge-123",
				requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
				responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			),
			BuildExchange(
				exchangeId: 11,
				url: "https://app.example.com/signin-callback?code=auth-code&state=session-state",
				requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
				responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			),
			BuildExchange(
				exchangeId: 12,
				url: "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signup_signin/oauth2/v2.0/token",
				requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
				responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
				method: "POST",
				formBody: new List<FormBodyEntry> {
					new("grant_type", "authorization_code"),
					new("code", "auth-code"),
					new("code_verifier", "verifier-123"),
				}
			)
		};

		try {
			// When: all-exchange sources output is written to disk.
			var sourcesPath = SourceReportWriter.WriteAllExchangeSourcesReport(outputBasePath, exchanges);
			var json = File.ReadAllText(sourcesPath);
			using var document = JsonDocument.Parse(json);

			// Then: each mapping includes the classifier result as a serialized string.
			var mappings = document.RootElement.GetProperty("Mappings");
			mappings.GetArrayLength().ShouldBe(3);
			mappings[0].GetProperty("RequestType").GetString().ShouldBe("AuthorizationRequest_AuthCodeWithPKCE");
			mappings[1].GetProperty("RequestType").GetString().ShouldBe("AuthorizationCallbackRequest");
			mappings[2].GetProperty("RequestType").GetString().ShouldBe("AuthorizationCodeTokenRequest");
		}
		finally {
			if (Directory.Exists(tempDirectory))
				Directory.Delete(tempDirectory, recursive: true);
		}
	}

	[Fact]
	public void WriteAllExchangeSourcesReport_SerializesRefreshTokenRequestRequestType() {
		// Given
		var tempDirectory = Path.Combine(Path.GetTempPath(), $"sws-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDirectory);
		var outputBasePath = Path.Combine(tempDirectory, "capture.exchanges.json");

		var exchanges = new List<Exchange> {
			BuildExchange(
				exchangeId: 20,
				url: "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signup_signin/oauth2/v2.0/token",
				requestCookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
				responseHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
				method: "POST",
				formBody: new List<FormBodyEntry> {
					new("grant_type", "refresh_token"),
					new("refresh_token", "refresh-token-value"),
				}
			)
		};

		try {
			// When
			var sourcesPath = SourceReportWriter.WriteAllExchangeSourcesReport(outputBasePath, exchanges);
			var json = File.ReadAllText(sourcesPath);
			using var document = JsonDocument.Parse(json);

			// Then
			var mappings = document.RootElement.GetProperty("Mappings");
			mappings.GetArrayLength().ShouldBe(1);
			mappings[0].GetProperty("RequestType").GetString().ShouldBe("RefreshTokenRequest");
		}
		finally {
			if (Directory.Exists(tempDirectory))
				Directory.Delete(tempDirectory, recursive: true);
		}
	}

	static Exchange BuildExchange(
		int exchangeId,
		string url,
		Dictionary<string, string> requestCookies,
		Dictionary<string, string> responseHeaders,
		string method = "GET",
		List<FormBodyEntry>? formBody = null
	) {
		var uri = new Uri(url);
		var queryParameters = ParseQueryParameters(uri);

		var request = new Request(
			$"{method} {url} HTTP/1.1",
			method,
			url,
			"HTTP/1.1",
			url,
			uri.Host,
			queryParameters,
			null,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
			requestCookies,
			new Body(0, null, "none", new List<string>()),
			null,
			formBody
		);

		var response = new Response(
			"HTTP/1.1 200 OK",
			200,
			"OK",
			responseHeaders,
			new Body(0, null, "none", new List<string>()),
			null,
			null
		);

		return new Exchange(exchangeId, null, null, request, response);
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