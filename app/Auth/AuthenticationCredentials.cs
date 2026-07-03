using System.Text.Json.Serialization;

namespace Auth;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "CredentialsKind")]
[JsonDerivedType(typeof(UsernamePasswordCredentials), nameof(UsernamePasswordCredentials))]
[JsonDerivedType(typeof(SessionCookieCredentials), nameof(SessionCookieCredentials))]
internal abstract record AuthenticationCredentials;

internal sealed record UsernamePasswordCredentials(
	string Username,
	string Password
) : AuthenticationCredentials;

internal sealed record SessionCookieCredentials(
	string CookieName,
	string CookieValue
) : AuthenticationCredentials;
