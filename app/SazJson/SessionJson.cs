namespace SazJson;

using Saz;

/// <summary>The shape of the human-readable JSON written for a <see cref="Session"/>.</summary>
internal sealed record SessionJson(
	string SourceFile,
	DateTimeOffset GeneratedUtc,
	GlobalHeadersGroup GlobalHeaders,
	List<Exchange> Exchanges
);
