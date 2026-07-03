using System.Text.Json.Serialization;

namespace Auth;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum RequestType {
	Unknown,

	// .well-known document that says where the endpoints are.
	Configuration,

	// Basic Authorization Request
	AuthorizationRequest_Unknown,

	// AuthCode + PKCE
	AuthorizationRequest_AuthCodeWithPKCE,

	// AuthCode
	AuthorizationRequest_AuthCode,

	// Implicit
	AuthorizationRequest_Implicit,

	// Hybrid
	AuthorizationRequest_Hybrid,

	// For Devices
	AuthorizationRequest_DeviceAuthorization,

	// the result of the AuthCodeRedirect back to the client app
	AuthorizationCallbackRequest,
		// → TokenExchangeInitiated
		// → SpaShellResponse or CallbackPageResponse

	// Client POSTS:
	// => grant_type=authorization_code  ->  I have an authorization code and want tokens."
	// => code
	// => code_verifier, 				 ->  This is Authorization Code Flow with PKCE.
	AuthorizationCodeTokenRequest,

	// Request the refresh token.
	RefreshTokenRequest,

	// grant_type=client_credentials
	ClientCredentialsTokenRequest,

	// grant_type=password (Resource Owner Password Credentials)
	PasswordTokenRequest,

	// grant_type=urn:ietf:params:oauth:grant-type:device_code (polling the token endpoint)
	DeviceCodeTokenRequest,

	// Logout / end-session request
	EndSessionRequest,
}
