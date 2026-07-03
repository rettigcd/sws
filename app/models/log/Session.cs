internal sealed record Session(
	int SessionId,
	DateTimeOffset? Timestamp,
	Metadata? Metadata,
	Request Request,
	Response Response
);
