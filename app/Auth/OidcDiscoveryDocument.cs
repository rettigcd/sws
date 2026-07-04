using System.Text.Json;

namespace Auth;

/// <summary>
/// A parsed OpenID Connect / OAuth2 discovery document
/// (from /.well-known/openid-configuration or /.well-known/oauth-authorization-server).
/// </summary>
internal sealed record OidcDiscoveryDocument(
	int SourceSessionId,
	string? Issuer,
	string? AuthorizationEndpoint,
	string? TokenEndpoint,
	string? JwksUri,
	string? UserInfoEndpoint,
	string? EndSessionEndpoint,
	string? DeviceAuthorizationEndpoint,
	List<string> ScopesSupported,
	List<string> ResponseTypesSupported,
	List<string> GrantTypesSupported,
	List<string> CodeChallengeMethodsSupported,
	List<string> ClaimsSupported
);

internal static class DiscoveryDocumentParser {

	/// <summary>
	/// Attempts to parse a session's response body as an OIDC/OAuth2 discovery document.
	/// Returns null unless the response looks like a real discovery document
	/// (has an issuer or an authorization_endpoint).
	/// </summary>
	public static OidcDiscoveryDocument? TryParse(Session session) {
		if (session.Response.ResponseJson is not { ValueKind: JsonValueKind.Object } json)
			return null;

		string? issuer = GetString(json, "issuer");
		string? authorizationEndpoint = GetString(json, "authorization_endpoint");
		if (issuer is null && authorizationEndpoint is null)
			return null;

		return new OidcDiscoveryDocument(
			session.SessionId,
			issuer,
			authorizationEndpoint,
			GetString(json, "token_endpoint"),
			GetString(json, "jwks_uri"),
			GetString(json, "userinfo_endpoint"),
			GetString(json, "end_session_endpoint"),
			GetString(json, "device_authorization_endpoint"),
			GetStringArray(json, "scopes_supported"),
			GetStringArray(json, "response_types_supported"),
			GetStringArray(json, "grant_types_supported"),
			GetStringArray(json, "code_challenge_methods_supported"),
			GetStringArray(json, "claims_supported")
		);
	}

	static string? GetString(JsonElement obj, string propertyName) {
		return obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
	}

	static List<string> GetStringArray(JsonElement obj, string propertyName) {
		var list = new List<string>();
		if (!obj.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
			return list;

		foreach (var item in value.EnumerateArray())
			if (item.ValueKind == JsonValueKind.String && item.GetString() is { } stringValue)
				list.Add(stringValue);

		return list;
	}
}
