internal sealed record Session(
	int SessionId,
	Metadata? Metadata,
	Request Request,
	Response Response
);
