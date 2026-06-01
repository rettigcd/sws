using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

internal static class SazArchiveReader {
	private static readonly Regex RawFilePattern = new(
		@"^raw\/(?<id>\d+)_(?<kind>[csm])\.(?<ext>txt|xml)$",
		RegexOptions.Compiled | RegexOptions.IgnoreCase
	);

	public static Dictionary<int, SessionRaw> LoadSessionRawMap(string sazPath) {
		using var stream = File.OpenRead(sazPath);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

		var map = new Dictionary<int, SessionRaw>();
		foreach (var entry in archive.Entries) {
			var match = RawFilePattern.Match(entry.FullName);
			if (!match.Success)
				continue;

			var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
			var kind = match.Groups["kind"].Value.ToLowerInvariant();

			if (!map.TryGetValue(id, out var sessionRaw)) {
				sessionRaw = new SessionRaw(id);
				map[id] = sessionRaw;
			}

			using var entryStream = entry.Open();
			using var memory = new MemoryStream();
			entryStream.CopyTo(memory);
			var bytes = memory.ToArray();

			switch (kind) {
				case "c":
					sessionRaw.ClientRequestBytes = bytes;
					break;
				case "s":
					sessionRaw.ServerResponseBytes = bytes;
					break;
				case "m":
					sessionRaw.MetadataBytes = bytes;
					break;
			}
		}

		return map;
	}
}