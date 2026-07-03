using System.Text.Json.Serialization;

namespace Minimal;

public class WellKnownConfiguration {

	[JsonPropertyName("issuer")]
	public string? Issuer { get; set; }

	[JsonPropertyName("authorization_endpoint")]
	public string? AuthorizationEndpoint { get; set; }

	[JsonPropertyName("token_endpoint")]
	public string? TokenEndpoint { get; set; }

	[JsonPropertyName("end_session_endpoint")]
	public string? EndSessionEndpoint { get; set; }

	[JsonPropertyName("jwks_uri")]
	public string? JwksUri { get; set; }

}
