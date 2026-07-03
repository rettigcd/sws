using System.Text.Json.Serialization;

namespace Auth;

/// <summary>
/// Classifies OIDC/OAuth2 authentication response types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ResponseType {
	Unknown,
	TokenResponse,
	AuthorizationRedirect,
	ErrorResponse,
	DeviceCodeResponse,
	ConfigurationResponse,
	SuccessResponse,
}
