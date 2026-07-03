using System.Text.Json.Serialization;

namespace Auth;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum VariableSource {
	QueryParameter,
	FormField,
	JsonBodyField,
	RequestHeader,
	ResponseHeader,
	Cookie,
	SetCookie,
	FragmentParameter,
	RedirectUrlParameter,
	Token,
}
