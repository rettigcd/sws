namespace sws.Tests;

using Shouldly;
using Xunit;

public class ChromeHeadersEngine_Tests {
	[Fact]
	public void BuildHeaderOverrides_RemovesChromeManagedHeaders() {
		var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
			["Accept"] = "text/html",
			["Accept-Language"] = "en-US,en;q=0.9",
			["cache-control"] = "no-cache",
			["Host"] = "example.com",
			["Pragma"] = "no-cache",
			["User-Agent"] = "custom-agent",
			["X-Correlation-Id"] = "abc-123",
			["Authorization"] = "Bearer token",
		};

		var overrides = ChromeHeadersEngine.BuildHeaderOverrides(captured);

		overrides.ContainsKey("Accept").ShouldBeFalse();
		overrides.ContainsKey("Accept-Language").ShouldBeFalse();
		overrides.ContainsKey("Cache-Control").ShouldBeFalse();
		overrides.ContainsKey("Host").ShouldBeFalse();
		overrides.ContainsKey("Pragma").ShouldBeFalse();
		overrides.ContainsKey("User-Agent").ShouldBeFalse();
		overrides["X-Correlation-Id"].ShouldBe("abc-123");
		overrides["Authorization"].ShouldBe("Bearer token");
	}

	[Fact]
	public void Build_FromSaz_DoesNotPersistChromeManagedHeadersInRequestPlan() {
		var repoRoot = FindRepoRoot();
		var sazPath = Path.Combine(repoRoot, "saz", "stage.saz");
		File.Exists(sazPath).ShouldBeTrue();

		var plan = SazPlanBuilder.Build(sazPath, new SazBuildOptions());
		plan.Sessions.Count.ShouldBeGreaterThan(0);

		var managedHeaderNames = new[] {
			"Accept",
			"Accept-Encoding",
			"Accept-Language",
			"Cache-Control",
			"Host",
			"Pragma",
			"Sec-Ch-Ua",
			"Sec-Ch-Ua-Mobile",
			"Sec-Ch-Ua-Platform",
			"Sec-Fetch-Dest",
			"Sec-Fetch-Mode",
			"Sec-Fetch-Site",
			"Sec-Fetch-User",
			"Upgrade-Insecure-Requests",
			"User-Agent",
		};

		foreach (var session in plan.Sessions) {
			var requestPlan = new RequestPlan(session.Request);
			foreach (var managedHeaderName in managedHeaderNames)
				requestPlan.Headers.ContainsKey(managedHeaderName).ShouldBeFalse();
		}
	}

	static string FindRepoRoot() {
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null) {
			var appProject = Path.Combine(directory.FullName, "app", "sws.csproj");
			var sazDir = Path.Combine(directory.FullName, "saz");
			if (File.Exists(appProject) && Directory.Exists(sazDir))
				return directory.FullName;

			directory = directory.Parent;
		}

		throw new InvalidOperationException("Could not locate repository root from test base directory.");
	}
}
