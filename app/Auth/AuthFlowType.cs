using System.Text.Json.Serialization;

namespace Auth;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum AuthFlowType {
	Unknown,
	AuthorizationCode,
	AuthorizationCodeWithPkce,
	Implicit,
	Hybrid,
	ClientCredentials,
	RefreshToken,
	DeviceCode,
	ResourceOwnerPasswordCredentials,
}
