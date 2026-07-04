using System.IO.Compression;
using System.Text;
using System.Text.Json;

internal static class BodyAnalyzer {
	public static Body BuildBodyPlan(Dictionary<string, string> headers, byte[] bodyBytes) {
		var contentType = GetHeader(headers, "Content-Type");
		var bodyText = DecodeBodyText(headers, bodyBytes);

		var format = GuessBodyFormat(contentType, bodyText);

		var schemaHints = new List<string>();
		if (format == "json" && !string.IsNullOrWhiteSpace(bodyText))
			schemaHints.AddRange(ExtractJsonTopLevelKeys(bodyText));
		else if (format == "form-url-encoded")
			schemaHints.AddRange(ParseFormKeys(bodyText));

		return new Body(
			bodyBytes.Length,
			contentType,
			format,
			schemaHints.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
		);
	}

	public static string DecodeBodyText(Dictionary<string, string> headers, byte[] bodyBytes) {
		if (bodyBytes.Length == 0)
			return string.Empty;

		var decodedBytes = DecompressIfBrotli(headers, bodyBytes);
		var encoding = DetectEncoding(headers) ?? Encoding.UTF8;
		return SafeDecode(decodedBytes, encoding);
	}

	static byte[] DecompressIfBrotli(Dictionary<string, string> headers, byte[] bodyBytes) {
		var contentEncoding = GetHeader(headers, "Content-Encoding");
		if (!string.Equals(contentEncoding, "br", StringComparison.OrdinalIgnoreCase))
			return bodyBytes;

		try {
			using var input = new MemoryStream(bodyBytes);
			using var brotli = new BrotliStream(input, CompressionMode.Decompress);
			using var output = new MemoryStream();
			brotli.CopyTo(output);
			return output.ToArray();
		}
		catch (Exception) {
			return bodyBytes;
		}
	}

	public static string? BuildResponseText(Dictionary<string, string> headers, string bodyText) {
		if (string.IsNullOrEmpty(bodyText))
			return null;

		var contentType = GetHeader(headers, "Content-Type") ?? string.Empty;
		if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) 
			|| contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
			|| contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) 
			|| contentType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase)
		) {
			return bodyText;
		}

		return null;
	}

	public static JsonElement? BuildResponseJson(string format, string bodyText) {
		if (format != "json" || string.IsNullOrWhiteSpace(bodyText))
			return null;

		try {
			using var doc = JsonDocument.Parse(bodyText);
			return doc.RootElement.Clone();
		}
		catch {
			return null;
		}
	}

	public static JsonElement? BuildRequestJsonBody(string format, string bodyText) {
		if (format != "json" || string.IsNullOrWhiteSpace(bodyText))
			return null;

		try {
			using var doc = JsonDocument.Parse(bodyText);
			return doc.RootElement.Clone();
		}
		catch {
			return null;
		}
	}

	public static List<FormBodyEntry>? BuildRequestFormBody(string format, string bodyText) {
		if (format != "form-url-encoded" || string.IsNullOrWhiteSpace(bodyText))
			return null;

		var pairs = ParseFormPairs(bodyText);
		return pairs.Count > 0 ? pairs : null;
	}

	static string? GetHeader(Dictionary<string, string> headers, string name) {
		return headers.TryGetValue(name, out var value) ? value : null;
	}

	static Encoding? DetectEncoding(Dictionary<string, string> headers) {
		var contentType = GetHeader(headers, "Content-Type");
		if (string.IsNullOrWhiteSpace(contentType))
			return null;

		var charsetIndex = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
		if (charsetIndex < 0)
			return null;

		var charset = contentType[(charsetIndex + "charset=".Length)..].Trim().Trim(';').Trim('"');
		if (charset.Length == 0)
			return null;

		try {
			return Encoding.GetEncoding(charset);
		}
		catch {
			return null;
		}
	}

	private static string SafeDecode(byte[] bytes, Encoding preferred) {
		try {
			return preferred.GetString(bytes);
		}
		catch {
			return Encoding.Latin1.GetString(bytes);
		}
	}

	private static string GuessBodyFormat(string? contentType, string bodyText) {
		var ct = contentType?.ToLowerInvariant() ?? string.Empty;

		if (ct.Contains("application/json") || bodyText.TrimStart().StartsWith('{') || bodyText.TrimStart().StartsWith('[')) {
			return "json";
		}

		if (ct.Contains("application/x-www-form-urlencoded")) {
			return "form-url-encoded";
		}

		if (ct.Contains("xml") || bodyText.TrimStart().StartsWith('<')) {
			return "xml";
		}

		if (ct.Contains("multipart/form-data")) {
			return "multipart";
		}

		if (string.IsNullOrWhiteSpace(bodyText)) {
			return "none";
		}

		return "text-or-binary";
	}

	private static List<string> ExtractJsonTopLevelKeys(string bodyText) {
		try {
			using var doc = JsonDocument.Parse(bodyText);
			if (doc.RootElement.ValueKind != JsonValueKind.Object) {
				return new List<string>();
			}

			return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
		}
		catch {
			return new List<string>();
		}
	}

	private static List<string> ParseFormKeys(string bodyText) {
		return ParseFormPairs(bodyText)
			.Select(pair => pair.Key)
			.ToList();
	}

	private static List<FormBodyEntry> ParseFormPairs(string bodyText) {
		var pairs = new List<FormBodyEntry>();

		foreach (var kvp in bodyText.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = kvp.Split('=', 2);
			if (parts.Length == 0) {
				continue;
			}

			var key = DecodeFormComponent(parts[0]);
			if (string.IsNullOrWhiteSpace(key)) {
				continue;
			}

			var value = parts.Length > 1 ? DecodeFormComponent(parts[1]) : string.Empty;
			pairs.Add(new FormBodyEntry(key, value));
		}

		return pairs;
	}

	private static string DecodeFormComponent(string value) {
		return Uri.UnescapeDataString(value.Replace('+', ' '));
	}
}