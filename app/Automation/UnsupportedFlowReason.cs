using System.Text.Json.Serialization;

namespace Automation;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum UnsupportedFlowReasonKind {
	UnsupportedFlowType,
	MfaRequired,
	WebAuthnOrFido2Required,
	JavaScriptRequired,
	CaptchaRequired,
	BrowserAutomationRequired,
	MissingRequiredEndpoint,
	MissingCredentials,
	Other,
}

internal sealed record UnsupportedFlowReason(
	UnsupportedFlowReasonKind Kind,
	string Message,

	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<string>? Details = null
);
