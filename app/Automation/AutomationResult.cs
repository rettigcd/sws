using System.Text.Json.Serialization;

namespace Automation;

internal sealed record TokenSet(
	string? AccessToken,
	string? TokenType,
	string? IdToken,
	string? RefreshToken,
	string? Scope,
	int? ExpiresInSeconds,
	DateTimeOffset? ExpiresAtUtc,
	DateTimeOffset ObtainedAtUtc
);

internal sealed record CapturedCookie(
	string Name,
	string Value,
	string? Domain,
	string? Path,
	bool Secure,
	bool HttpOnly,
	DateTimeOffset? Expires
);

internal sealed record Claim(string Name, string Value);

/// <summary>
/// One entry in the human-readable diagnostic trail. Description must never interpolate a
/// raw secret/token VALUE - names/roles/counts only. RequestUrl is always query-stripped.
/// </summary>
internal sealed record AutomationStep(
	int Order,
	string Description,
	bool Success,
	DateTimeOffset TimestampUtc,

	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? HttpStatusCode = null,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestUrl = null
);

internal sealed record AutomationResult(
	bool Success,
	string FlowId,
	Auth.AuthFlowType FlowType,

	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TokenSet? Tokens,
	List<CapturedCookie> Cookies,
	List<Claim> Claims,
	List<AutomationStep> Steps,
	List<ResolvedVariable> Variables,

	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UnsupportedFlowReason? UnsupportedReason = null,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorMessage = null
);
