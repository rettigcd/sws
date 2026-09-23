namespace Auth;

using Saz;

/// <summary>An <see cref="Exchange"/> together with the OIDC/OAuth2 request and response types the classifier assigned to it.</summary>
internal sealed record ClassifiedExchange(
	Exchange Exchange,
	RequestType RequestType,
	ResponseType ResponseType
) {
	public int ExchangeId => Exchange.ExchangeId;
}
