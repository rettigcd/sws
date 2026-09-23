namespace Saz;

using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

internal static class SazArchiveReader {
	private static readonly Regex RawFilePattern = new(
		@"^raw\/(?<id>\d+)_(?<kind>[csm])\.(?<ext>txt|xml)$",
		RegexOptions.Compiled | RegexOptions.IgnoreCase
	);

	public static Dictionary<int, ExchangeRaw> LoadExchangeRawMap(string sazPath) {
		using var stream = File.OpenRead(sazPath);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

		var map = new Dictionary<int, ExchangeRaw>();
		foreach (var entry in archive.Entries) {
			var match = RawFilePattern.Match(entry.FullName);
			if (!match.Success)
				continue;

			int id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
			string kind = match.Groups["kind"].Value.ToLowerInvariant();

			if (!map.TryGetValue(id, out var exchangeRaw)) {
				exchangeRaw = new ExchangeRaw(id);
				map[id] = exchangeRaw;
			}

			using var entryStream = entry.Open();
			using var memory = new MemoryStream();
			entryStream.CopyTo(memory);
			var bytes = memory.ToArray();

			switch (kind) {
				case "c":
					exchangeRaw.ClientRequestBytes = bytes;
					break;
				case "s":
					exchangeRaw.ServerResponseBytes = bytes;
					break;
				case "m":
					exchangeRaw.MetadataBytes = bytes;
					break;
			}
		}

		return map;
	}
}