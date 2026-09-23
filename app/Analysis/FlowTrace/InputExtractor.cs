namespace Analysis;

using Saz;

/// <summary>Lists every value a request carries, so each can be traced back to where it came from.</summary>
internal static class InputExtractor {

	public static List<FlowInput> Extract(Exchange exchange) {
		var request = exchange.Request;
		var inputs = new List<FlowInput>();

		if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) {
			string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < segments.Length; i++)
				inputs.Add(new FlowInput(InputKind.PathSegment, $"path[{i}]", Uri.UnescapeDataString(segments[i])));
		}

		foreach (var parameter in request.QueryParameters)
			inputs.Add(new FlowInput(InputKind.QueryParameter, parameter.Key, parameter.Value));

		if (request.JsonBody is { } json)
			foreach (var leaf in JsonLeaves.Enumerate(json))
				inputs.Add(new FlowInput(InputKind.JsonBodyField, leaf.Path, leaf.Value));

		if (request.FormBody is not null)
			foreach (var entry in request.FormBody)
				inputs.Add(new FlowInput(InputKind.FormField, entry.Key, entry.Value));

		foreach (var cookie in request.Cookies)
			inputs.Add(new FlowInput(InputKind.Cookie, cookie.Key, cookie.Value));

		foreach (var header in request.Headers) {
			// Cookies are listed one by one above; per-request headers (Content-Length, Date, ...) are not inputs.
			if (string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase) || DynamicHeaders.IsDynamic(header.Key))
				continue;

			inputs.Add(new FlowInput(InputKind.RequestHeader, header.Key, header.Value));
		}

		return inputs;
	}
}
