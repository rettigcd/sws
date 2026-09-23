namespace Analysis;

using Saz;

/// <summary>Drops exchanges that are usually noise for analysis (CONNECT tunnels, CSS, media, sourcemaps, excluded hosts).</summary>
internal static class ExchangeFilter {
	private static readonly HashSet<string> MediaPathExtensions =
	[
		".png",
		".jpg",
		".jpeg",
		".gif",
		".webp",
		".bmp",
		".ico",
		".svg",
		".mp4",
		".webm",
		".avi",
		".mov",
		".mp3",
		".wav",
		".ogg",
		".m4a",
		".m4v",
		".woff",
		".woff2",
		".ttf",
		".otf",
		".eot",
	];

	static readonly HashSet<string> ExcludedHosts =
	[
		"svcs.tql.com",
	];


	public static List<Exchange> Apply(IEnumerable<Exchange> exchanges, ExchangeFilterOptions options) {
		return exchanges
			.Where(exchange =>
				   (options.IncludeConnect || !IsConnectExchange(exchange))
				&& (options.IncludeCss || !IsCssExchange(exchange))
				&& (options.IncludeMedia || !IsMediaExchange(exchange))
				&& (options.IncludeSourcemaps || !IsSourcemapExchange(exchange))
				&& !IsExcludedHostExchange(exchange)
			)
			.ToList();
	}

	static bool IsConnectExchange(Exchange exchange) {
		return string.Equals(exchange.Request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase);
	}

	static bool IsCssExchange(Exchange exchange) {
		if (HasPathExtension(exchange.Request.Url, ".css"))
			return true;

		if (exchange.Response.Headers.TryGetValue("Content-Type", out string? responseContentType) &&
			responseContentType.Contains("text/css", StringComparison.OrdinalIgnoreCase)) {
			return true;
		}

		return false;
	}

	static bool IsMediaExchange(Exchange exchange) {
		if (HasPathExtension(exchange.Request.Url, MediaPathExtensions))
			return true;

		if (exchange.Response.Headers.TryGetValue("Content-Type", out string? responseContentType)) {
			if (responseContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) 
				|| responseContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) 
				|| responseContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
				|| responseContentType.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
			)
				return true;

			if (responseContentType.Contains("application/font", StringComparison.OrdinalIgnoreCase))
				return true;

		}

		return false;
	}

	static bool IsSourcemapExchange(Exchange exchange) {
		return HasPathExtension(exchange.Request.Url, ".map");
	}

	static bool IsExcludedHostExchange(Exchange exchange) {
		string host = GetNormalizedHost(exchange);
		return !string.IsNullOrWhiteSpace(host) && ExcludedHosts.Contains(host);
	}

	static string GetNormalizedHost(Exchange exchange) {
		if (Uri.TryCreate(exchange.Request.Url, UriKind.Absolute, out var uri))
			return uri.Host.ToLowerInvariant();

		string host = exchange.Request.Host?.Trim() ?? string.Empty;
		if (host.Length == 0)
			return string.Empty;

		if (!host.StartsWith("[", StringComparison.Ordinal)) {
			int firstColon = host.IndexOf(":", StringComparison.Ordinal);
			int lastColon = host.LastIndexOf(":", StringComparison.Ordinal);
			if (firstColon > 0 && firstColon == lastColon)
				host = host[..firstColon];
		}

		return host.ToLowerInvariant();
	}

	static bool HasPathExtension(string urlOrPath, string extension) {
		if (string.IsNullOrWhiteSpace(urlOrPath))
			return false;

		if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri))
			return uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

		string path = urlOrPath.Split('?', 2)[0];
		return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
	}

	static bool HasPathExtension(string urlOrPath, HashSet<string> extensions) {
		if (string.IsNullOrWhiteSpace(urlOrPath))
			return false;

		string path;
		if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri))
			path = uri.AbsolutePath;
		else
			path = urlOrPath.Split('?', 2)[0];

		string extension = Path.GetExtension(path);
		if (string.IsNullOrWhiteSpace(extension))
			return false;

		return extensions.Contains(extension.ToLowerInvariant());
	}
}
