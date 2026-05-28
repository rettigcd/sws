internal readonly record struct HttpMessageParts(
	string StartLine,
	Dictionary<string, string> Headers,
	byte[] BodyBytes
);
