namespace Automation;

/// <summary>Applies realistic browser-like headers to freshly-built requests, reusing the existing engine.</summary>
internal static class HeaderHelpers {

	public static void Apply(HttpRequestMessage request) {
		foreach (var header in ChromeHeadersEngine.BuildHeaders(request.Method.Method, request.RequestUri)) {
			if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
				continue;

			request.Headers.Remove(header.Key);
			request.Headers.TryAddWithoutValidation(header.Key, header.Value);
		}
	}
}
