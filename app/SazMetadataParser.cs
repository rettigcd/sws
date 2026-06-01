using System.Globalization;
using System.Text;
using System.Xml.Linq;

internal static class SazMetadataParser {
	private static readonly object MalformedMetadataLogLock = new();
	private static readonly string MalformedMetadataLogPath = Path.Combine(Environment.CurrentDirectory, "malformed-metadata.log");

	public static Metadata Parse(byte[]? metadataBytes, int sessionId) {
		if (metadataBytes is null || metadataBytes.Length == 0)
			return new Metadata(new Dictionary<string, string>(), new Dictionary<string, string>());

		var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var timers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		XDocument doc;
		try {
			using var stream = new MemoryStream(metadataBytes);
			doc = XDocument.Load(stream, LoadOptions.None);
		}
		catch (Exception ex) {
			LogMalformedMetadata(sessionId, metadataBytes, ex);
			return new Metadata(flags, timers);
		}

		foreach (var flag in doc.Descendants("SessionFlag")) {
			var key = flag.Attribute("N")?.Value;
			var value = flag.Attribute("V")?.Value;
			if (!string.IsNullOrWhiteSpace(key) && value is not null)
				flags[key] = value;
		}

		var timersElement = doc.Descendants("SessionTimers").FirstOrDefault();
		if (timersElement is not null)
			foreach (var attr in timersElement.Attributes())
				timers[attr.Name.LocalName] = attr.Value;

		return new Metadata(flags, timers);
	}

	private static void LogMalformedMetadata(int sessionId, byte[] metadataBytes, Exception ex) {
		var utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
		var utf8 = Encoding.UTF8.GetString(metadataBytes);
		var latin1 = Encoding.Latin1.GetString(metadataBytes);
		var base64 = Convert.ToBase64String(metadataBytes);

		var entry = $"[{utc}] Session {sessionId} malformed metadata. "
			+ $"Length={metadataBytes.Length}. Error={ex.GetType().Name}: {ex.Message}{Environment.NewLine}"
			+ $"UTF8:{Environment.NewLine}{utf8}{Environment.NewLine}"
			+ $"Latin1:{Environment.NewLine}{latin1}{Environment.NewLine}"
			+ $"Base64:{Environment.NewLine}{base64}{Environment.NewLine}"
			+ $"{new string('-', 80)}{Environment.NewLine}";

		lock (MalformedMetadataLogLock) {
			File.AppendAllText(MalformedMetadataLogPath, entry, Encoding.UTF8);
		}
	}
}