namespace Saz;

/// <summary>A series of exchanges (request/response pairs), such as the contents of a .saz capture.</summary>
internal sealed record Session(
	string SourceFile,
	List<Exchange> Exchanges
);
