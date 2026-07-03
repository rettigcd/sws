namespace sws.Tests;

using Auth;
using Shouldly;
using Xunit;
using static sws.Tests.TestSessionBuilder;

public class B2cEnricher_Tests {

	[Fact]
	public void Detect_ExtractsTenantAndPolicy_FromPathSegments() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signup_signin/oauth2/v2.0/authorize?client_id=abc&response_type=code&state=st-1"),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		var flow = result.Flows[0];
		flow.IsAzureB2c.ShouldBeTrue();
		flow.B2cDetails.ShouldNotBeNull();
		flow.B2cDetails!.Tenant.ShouldBe("tenant.onmicrosoft.com");
		flow.B2cDetails.Policy.ShouldBe("b2c_1a_signup_signin");
	}

	[Fact]
	public void Detect_ExtractsPolicy_FromPQueryParameter() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://tenant.b2clogin.com/authorize?client_id=abc&response_type=code&state=st-1&p=B2C_1_signupsignin1"),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows[0].B2cDetails!.Policy.ShouldBe("B2C_1_signupsignin1");
	}

	[Fact]
	public void Detect_CollectsB2cCookies_IntoB2cDetails() {
		// Given
		var sessions = new List<Session> {
			BuildSession(
				1,
				"GET",
				"https://tenant.b2clogin.com/tenant.onmicrosoft.com/b2c_1a_signup_signin/oauth2/v2.0/authorize?client_id=abc&response_type=code&state=st-1",
				cookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
					["x-ms-cpim-csrf"] = "csrf-token",
					["unrelated-cookie"] = "value",
				}
			),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		var b2cCookies = result.Flows[0].B2cDetails!.B2cCookies;
		b2cCookies.ShouldContainKey("x-ms-cpim-csrf");
		b2cCookies.ShouldNotContainKey("unrelated-cookie");
	}

	[Fact]
	public void Detect_DoesNotFlagNonB2cFlow_AsAzureB2c() {
		// Given
		var sessions = new List<Session> {
			BuildSession(1, "GET", "https://login.example.com/connect/authorize?client_id=abc&response_type=code&state=st-1"),
		};

		// When
		var result = AuthFlowDetector.Detect(sessions);

		// Then
		result.Flows[0].IsAzureB2c.ShouldBeFalse();
		result.Flows[0].B2cDetails.ShouldBeNull();
	}
}
