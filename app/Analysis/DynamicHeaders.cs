namespace Analysis;

/// <summary>Headers whose captured values are per-request (length, timestamps, trace ids) and should not be replayed verbatim.</summary>
internal static class DynamicHeaders {

	static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase) {
		"content-length",
		"date",
		"x-request-id",
		"traceparent",
		"x-correlation-id",
		"x-amzn-trace-id",
		"x-forwarded-for",
		"x-forwarded-proto",
		"x-real-ip",
	};

	public static bool IsDynamic(string headerName) {
		return Names.Contains(headerName);
	}

	public static Dictionary<string, string> Without(IReadOnlyDictionary<string, string> headers) {
		return headers
			.Where(header => !IsDynamic(header.Key))
			.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
	}
}
