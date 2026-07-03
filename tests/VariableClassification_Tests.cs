namespace sws.Tests;

using System.Text.Json;
using Auth;
using Shouldly;
using Xunit;
using static sws.Tests.TestSessionBuilder;

public class VariableClassification_Tests {

	static List<Variable> BuildFlowVariables() {
		var sessions = new List<Session> {
			BuildSession(
				1,
				"GET",
				"https://login.example.com/connect/authorize?client_id=my-client&response_type=code&code_challenge=chal-1&nonce=nonce-1&state=state-1&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcallback&scope=openid%20profile",
				cookies: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["_ga"] = "GA1.1.111" }
			),
			BuildSession(2, "GET", "https://app.example.com/callback?code=auth-code-1&state=state-1"),
			BuildSession(
				3,
				"POST",
				"https://login.example.com/connect/token",
				formBody: new List<FormBodyEntry> {
					new("grant_type", "authorization_code"),
					new("code", "auth-code-1"),
					new("code_verifier", "verifier-1"),
					new("client_secret", "secret-1"),
				},
				requestHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
					["Authorization"] = "Basic QWxhZGRpbjpvcGVuc2VzYW1l",
				},
				responseJson: JsonDocument.Parse("""
					{ "access_token": "at-1", "refresh_token": "rt-1", "id_token": "idt-1" }
				""").RootElement.Clone()
			),
		};

		var result = AuthFlowDetector.Detect(sessions);
		result.Flows.Count.ShouldBe(1);
		return result.Flows[0].Variables;
	}

	[Fact]
	public void ClientId_IsClassifiedAsConfiguration() {
		BuildFlowVariables().First(v => v.Name == "client_id").Category.ShouldBe(VariableCategory.Configuration);
	}

	[Fact]
	public void RedirectUriAndScope_AreClassifiedAsConfiguration() {
		var variables = BuildFlowVariables();
		variables.First(v => v.Name == "redirect_uri").Category.ShouldBe(VariableCategory.Configuration);
		variables.First(v => v.Name == "scope").Category.ShouldBe(VariableCategory.Configuration);
	}

	[Fact]
	public void ClientSecret_IsClassifiedAsSecret() {
		BuildFlowVariables().First(v => v.Name == "client_secret").Category.ShouldBe(VariableCategory.Secret);
	}

	[Fact]
	public void BasicAuthorizationHeader_IsClassifiedAsSecret() {
		BuildFlowVariables().First(v => v.Name == "Authorization" && v.Source == VariableSource.RequestHeader).Category.ShouldBe(VariableCategory.Secret);
	}

	[Fact]
	public void StateNonceAndCodeVerifier_AreClassifiedAsGeneratedPerFlow() {
		var variables = BuildFlowVariables();
		variables.First(v => v.Name == "state").Category.ShouldBe(VariableCategory.GeneratedPerFlow);
		variables.First(v => v.Name == "nonce").Category.ShouldBe(VariableCategory.GeneratedPerFlow);
		variables.First(v => v.Name == "code_verifier").Category.ShouldBe(VariableCategory.GeneratedPerFlow);
	}

	[Fact]
	public void CodeAccessTokenAndRefreshToken_AreClassifiedAsServerGenerated() {
		var variables = BuildFlowVariables();
		variables.First(v => v.Name == "code").Category.ShouldBe(VariableCategory.ServerGenerated);
		variables.First(v => v.Name == "access_token").Category.ShouldBe(VariableCategory.ServerGenerated);
		variables.First(v => v.Name == "refresh_token").Category.ShouldBe(VariableCategory.ServerGenerated);
		variables.First(v => v.Name == "id_token").Category.ShouldBe(VariableCategory.ServerGenerated);
	}

	[Fact]
	public void CodeChallenge_IsClassifiedAsDerivedFromCodeVerifier() {
		var variable = BuildFlowVariables().First(v => v.Name == "code_challenge");
		variable.Category.ShouldBe(VariableCategory.Derived);
		variable.DerivedFromVariableName.ShouldBe("code_verifier");
	}

	[Fact]
	public void AnalyticsCookie_IsClassifiedAsNonParticipating() {
		BuildFlowVariables().First(v => v.Name == "_ga").Category.ShouldBe(VariableCategory.NonParticipating);
	}
}
