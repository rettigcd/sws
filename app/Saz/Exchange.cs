namespace Saz;

/// <summary>One request/response pair. (The .saz format calls this a "session".)</summary>
internal sealed record Exchange(
	int ExchangeId,
	DateTimeOffset? Timestamp,
	Metadata? Metadata,
	Request Request,
	Response Response
);
