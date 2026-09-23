internal static class ChromeHeadersEngine {
	static readonly string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36";
	static readonly string DefaultSecChUa = "\"Google Chrome\";v=\"137\", \"Chromium\";v=\"137\", \"Not/A)Brand\";v=\"24\"";
	static readonly string DefaultSecChUaMobile = "?0";
	static readonly string DefaultSecChUaPlatform = "\"Windows\"";

	static readonly HashSet<string> EngineManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
	{
		"Accept",
		"Accept-Encoding",
		"Accept-Language",
		"Cache-Control",
		"Connection",
		"Host",
		"Pragma",
		"Priority",
		"Sec-Ch-Ua",
		"Sec-Ch-Ua-Mobile",
		"Sec-Ch-Ua-Platform",
		"Sec-Fetch-Dest",
		"Sec-Fetch-Mode",
		"Sec-Fetch-Site",
		"Sec-Fetch-User",
		"Upgrade-Insecure-Requests",
		"User-Agent",
	};

	public static bool IsEngineManagedHeader(string headerName) {
		return EngineManagedHeaders.Contains(headerName);
	}

	public static Dictionary<string, string> BuildHeaderOverrides(IReadOnlyDictionary<string, string> capturedHeaders) {
		return capturedHeaders
			.Where(header => !IsEngineManagedHeader(header.Key))
			.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
	}

	public static Dictionary<string, string> BuildHeaders(string method, Uri? requestUri) {
		bool isDocumentNavigation = IsDocumentNavigationRequest(method, requestUri);
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
			["Accept-Encoding"] = "gzip, deflate, br, zstd",
			["Accept-Language"] = "en-US,en;q=0.9",
			["Cache-Control"] = "no-cache",
			["Connection"] = "keep-alive",
			["Pragma"] = "no-cache",
			["Priority"] = isDocumentNavigation ? "u=0, i" : "u=1, i",
			["Sec-Ch-Ua"] = DefaultSecChUa,
			["Sec-Ch-Ua-Mobile"] = DefaultSecChUaMobile,
			["Sec-Ch-Ua-Platform"] = DefaultSecChUaPlatform,
			["User-Agent"] = DefaultUserAgent,
		};

		if (isDocumentNavigation) {
			headers["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";
			headers["Sec-Fetch-Dest"] = "document";
			headers["Sec-Fetch-Mode"] = "navigate";
			headers["Sec-Fetch-Site"] = "none";
			headers["Sec-Fetch-User"] = "?1";
			headers["Upgrade-Insecure-Requests"] = "1";
		}
		else {
			headers["Accept"] = "*/*";
			headers["Sec-Fetch-Dest"] = "empty";
			headers["Sec-Fetch-Mode"] = "cors";
			headers["Sec-Fetch-Site"] = "same-origin";
		}

		return headers;
	}

	static bool IsDocumentNavigationRequest(string method, Uri? requestUri) {
		if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
			return false;

		if (requestUri is null)
			return true;

		string path = requestUri.AbsolutePath;
		if (string.IsNullOrWhiteSpace(path) || path == "/")
			return true;

		string extension = Path.GetExtension(path);
		if (string.IsNullOrWhiteSpace(extension))
			return true;

		return extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
			|| extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
			|| extension.Equals(".aspx", StringComparison.OrdinalIgnoreCase)
			|| extension.Equals(".php", StringComparison.OrdinalIgnoreCase);
	}
}
