using System.Net;
using System.Text.Json;

namespace Automation;

/// <summary>Shared token-endpoint response parsing used by both flow handlers.</summary>
internal static class TokenExchange {

	public static async Task<AutomationResult> ParseResponseAsync(
		Auth.DetectedAuthenticationFlow flow,
		AutomationStepLog stepLog,
		List<ResolvedVariable> variables,
		IAuthHttpClient httpClient,
		HttpResponseMessage tokenResponse,
		CancellationToken cancellationToken
	) {
		var body = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		var obtainedAtUtc = DateTimeOffset.UtcNow;

		JsonDocument? document = null;
		try {
			document = JsonDocument.Parse(body);
		}
		catch (JsonException) {
			// leave document null; handled below
		}

		if (!tokenResponse.IsSuccessStatusCode || document is null) {
			var errorMessage = ExtractErrorMessage(document, tokenResponse.StatusCode);
			stepLog.Record("Token exchange failed.", success: false, httpStatusCode: (int)tokenResponse.StatusCode);
			return new AutomationResult(false, flow.FlowId, flow.FlowType, null, ExtractCookies(httpClient), [], stepLog.ToList(), variables, null, errorMessage);
		}

		using var documentScope = document;
		var root = document.RootElement;

		var tokens = new TokenSet(
			GetString(root, "access_token"),
			GetString(root, "token_type"),
			GetString(root, "id_token"),
			GetString(root, "refresh_token"),
			GetString(root, "scope"),
			GetInt(root, "expires_in"),
			GetInt(root, "expires_in") is int seconds ? obtainedAtUtc.AddSeconds(seconds) : null,
			obtainedAtUtc
		);

		var presentFields = new[] { "access_token", "id_token", "refresh_token" }.Where(name => root.TryGetProperty(name, out _)).ToList();
		stepLog.Record(
			presentFields.Count > 0 ? $"Token response included {string.Join(", ", presentFields)}." : "Token response returned no token fields.",
			httpStatusCode: (int)tokenResponse.StatusCode
		);

		var claims = JwtDecoder.Decode(tokens.IdToken);
		if (claims.Count == 0)
			claims = JwtDecoder.Decode(tokens.AccessToken);

		return new AutomationResult(true, flow.FlowId, flow.FlowType, tokens, ExtractCookies(httpClient), claims, stepLog.ToList(), variables, null, null);
	}

	public static List<CapturedCookie> ExtractCookies(IAuthHttpClient httpClient) {
		var cookies = new List<CapturedCookie>();
		foreach (Cookie cookie in httpClient.Cookies.GetAllCookies()) {
			cookies.Add(new CapturedCookie(
				cookie.Name,
				cookie.Value,
				string.IsNullOrEmpty(cookie.Domain) ? null : cookie.Domain,
				string.IsNullOrEmpty(cookie.Path) ? null : cookie.Path,
				cookie.Secure,
				cookie.HttpOnly,
				cookie.Expires == default ? null : cookie.Expires
			));
		}

		return cookies;
	}

	static string ExtractErrorMessage(JsonDocument? document, HttpStatusCode statusCode) {
		if (document is not null) {
			if (document.RootElement.TryGetProperty("error_description", out var description) && description.ValueKind == JsonValueKind.String)
				return description.GetString()!;

			if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
				return error.GetString()!;
		}

		return $"Token endpoint returned status {(int)statusCode}.";
	}

	static string? GetString(JsonElement root, string name) {
		return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
	}

	static int? GetInt(JsonElement root, string name) {
		if (!root.TryGetProperty(name, out var value))
			return null;

		return value.ValueKind switch {
			JsonValueKind.Number when value.TryGetInt32(out var intValue) => intValue,
			JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
			_ => null,
		};
	}
}
