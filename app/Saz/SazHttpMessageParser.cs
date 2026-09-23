using System.Globalization;
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

		if (headers.TryGetValue("Transfer-Encoding", out string? transferEncoding)
			&& transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase)) {
			bodyBytes = Dechunk(bodyBytes);
		}

		return new HttpMessageParts(startLine, headers, bodyBytes);
	}

	/// <summary>Strips HTTP chunked-transfer-encoding framing (chunk-size lines and trailing CRLFs), leaving raw body bytes.</summary>
	private static byte[] Dechunk(byte[] body) {
		var crlf = new byte[] { 13, 10 };
		using var output = new MemoryStream();

		int pos = 0;
		while (pos < body.Length) {
			int lineEnd = IndexOf(body, crlf, startIndex: pos);
			if (lineEnd < 0)
				break;

			string sizeLine = Encoding.ASCII.GetString(body, pos, lineEnd - pos).Split(';')[0].Trim();
			if (!int.TryParse(sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int chunkSize))
				break;

			if (chunkSize == 0)
				break;

			int chunkStart = lineEnd + crlf.Length;
			if (chunkStart + chunkSize > body.Length)
				break;

			output.Write(body, chunkStart, chunkSize);
			pos = chunkStart + chunkSize + crlf.Length;
		}

		return output.ToArray();
	}

	private static (byte[] headerBytes, byte[] bodyBytes) FindHeaderBodySeparator(byte[] rawBytes) {
		var crlf = new byte[] { 13, 10, 13, 10 };
		var lf = new byte[] { 10, 10 };

		int idx = IndexOf(rawBytes, crlf);
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
		return IndexOf(data, marker, 0);
	}

	private static int IndexOf(byte[] data, byte[] marker, int startIndex) {
		if (marker.Length == 0 || data.Length < marker.Length)
			return -1;

		for (int i = startIndex; i <= data.Length - marker.Length; i++) {
			bool matched = true;
			for (int j = 0; j < marker.Length; j++) {
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

		foreach (string rawLine in headerLines) {
			if (string.IsNullOrEmpty(rawLine))
				continue;

			if ((rawLine.StartsWith(' ') || rawLine.StartsWith('\t')) && currentHeader is not null) {
				headers[currentHeader] = headers[currentHeader] + " " + rawLine.Trim();
				continue;
			}

			int idx = rawLine.IndexOf(':');
			if (idx <= 0)
				continue;

			string name = rawLine[..idx].Trim();
			string value = rawLine[(idx + 1)..].Trim();
			if (name.Length == 0)
				continue;

			currentHeader = name;
			headers[name] = headers.TryGetValue(name, out string? existingValue)
				? existingValue + "\n" + value
				: value;
		}

		return headers;
	}
}