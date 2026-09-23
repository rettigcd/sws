namespace SazJson;

using Saz;

/// <summary>Moves request headers that every exchange shares into a single group, so the JSON is shorter to read.</summary>
internal static class GlobalHeaders {

	public static GlobalHeadersGroup Find(IReadOnlyList<Exchange> exchanges) {
		if (exchanges.Count == 0)
			return new GlobalHeadersGroup("global-headers", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

		var commonHeaders = exchanges[0].Request.Headers
			.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);

		foreach (var exchange in exchanges.Skip(1)) {
			var toRemove = commonHeaders
				.Where(commonHeader =>
					!exchange.Request.Headers.TryGetValue(commonHeader.Key, out string? value) ||
					!string.Equals(value, commonHeader.Value, StringComparison.Ordinal))
				.Select(commonHeader => commonHeader.Key)
				.ToList();

			foreach (string key in toRemove)
				commonHeaders.Remove(key);
		}

		return new GlobalHeadersGroup("global-headers", SortHeaders(commonHeaders));
	}

	public static List<Exchange> Remove(
		IReadOnlyList<Exchange> exchanges,
		Dictionary<string, string> globalHeaders) {
		if (globalHeaders.Count == 0)
			return [.. exchanges];

		return exchanges
			.Select(exchange => {
				var requestHeaders = exchange.Request.Headers
					.Where(header => !globalHeaders.ContainsKey(header.Key))
					.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);

				var request = exchange.Request with {
					Headers = SortHeaders(requestHeaders),
				};

				return exchange with { Request = request };
			})
			.ToList();
	}

	private static Dictionary<string, string> SortHeaders(Dictionary<string, string> headers) {
		return headers
			.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
	}
}
