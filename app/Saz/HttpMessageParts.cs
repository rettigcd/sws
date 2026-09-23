// intermediate result from splitting up a Request OR Response
// into its headers & body
internal readonly record struct HttpMessageParts(
	string StartLine,
	Dictionary<string, string> Headers,
	byte[] BodyBytes
);
