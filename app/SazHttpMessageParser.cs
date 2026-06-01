using System.Text;

internal static class SazHttpMessageParser {
	public static HttpMessageParts Parse(byte[]? rawBytes, bool isRequest) {
		if (rawBytes is null || rawBytes.Length == 0)
			return new HttpMessageParts(string.Empty, new Dictionary<string, string>(), Array.Empty<byte>());

		(byte[] headerBytes, byte[] bodyBytes) = FindHeaderBodySeparator(rawBytes);

		string headerText = Encoding.Latin1.GetString(headerBytes);
		string[] lines = headerText.Split(["\r\n", "\n"], StringSplitOptions.None);
		string startLine = lines.FirstOrDefault() ?? string.Empty;

		Dictionary<string, string> headers = ParseHeaders(lines.Skip(1));

		if (isRequest && !headers.ContainsKey("Host") && startLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase)) {
			string? target = startLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
			if (!string.IsNullOrWhiteSpace(target))
				headers["Host"] = target;
		}

		return new HttpMessageParts(startLine, headers, bodyBytes);
	}

	private static (byte[] headerBytes, byte[] bodyBytes) FindHeaderBodySeparator(byte[] rawBytes) {
		var crlf = new byte[] { 13, 10, 13, 10 };
		var lf = new byte[] { 10, 10 };

		var idx = IndexOf(rawBytes, crlf);
		if (idx >= 0) {
			return (
				rawBytes[..idx],
				rawBytes[(idx + crlf.Length)..]
			);
		}

		idx = IndexOf(rawBytes, lf);
		if (idx >= 0) {
			return (
				rawBytes[..idx],
				rawBytes[(idx + lf.Length)..]
			);
		}

		return (rawBytes, Array.Empty<byte>());
	}

	private static int IndexOf(byte[] data, byte[] marker) {
		if (marker.Length == 0 || data.Length < marker.Length)
			return -1;

		for (var i = 0; i <= data.Length - marker.Length; i++) {
			var matched = true;
			for (var j = 0; j < marker.Length; j++) {
				if (data[i + j] != marker[j]) {
					matched = false;
					break;
				}
			}

			if (matched)
				return i;
		}

		return -1;
	}

	private static Dictionary<string, string> ParseHeaders(IEnumerable<string> headerLines) {
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string? currentHeader = null;

		foreach (var rawLine in headerLines) {
			if (string.IsNullOrEmpty(rawLine))
				continue;

			if ((rawLine.StartsWith(' ') || rawLine.StartsWith('\t')) && currentHeader is not null) {
				headers[currentHeader] = headers[currentHeader] + " " + rawLine.Trim();
				continue;
			}

			var idx = rawLine.IndexOf(':');
			if (idx <= 0)
				continue;

			var name = rawLine[..idx].Trim();
			var value = rawLine[(idx + 1)..].Trim();
			if (name.Length == 0)
				continue;

			currentHeader = name;
			headers[name] = value;
		}

		return headers;
	}
}