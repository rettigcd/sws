namespace Analysis;

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Saz;

internal static class SourceReportWriter {
	public static string WriteAllExchangeSourcesReport(string outputBasePath, IReadOnlyList<Exchange> exchanges) {
		string outputPath = DeriveSiblingOutputPath(outputBasePath, ".sources.json");
		var options = new JsonSerializerOptions {
			WriteIndented = true,
			IndentCharacter = '\t',
			IndentSize = 1,
			DefaultIgnoreCondition = JsonIgnoreCondition.Never,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};

		var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var unsourcedCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var azureB2cSourceContext = SourceReportBuilder.BuildAzureB2cSourceContext(exchanges);
		var mappings = exchanges
			.Select((exchange, index) => SourceReportBuilder.BuildExchangeSourcesReport(index, exchanges, missing, unsourcedCookies, azureB2cSourceContext))
			.ToList();

		var sortedUnsourcedCookies = unsourcedCookies
			.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

		var report = new ExchangeSourcesBatchReport(
			Path.GetFullPath(outputBasePath),
			missing,
			sortedUnsourcedCookies,
			mappings
		);

		File.WriteAllText(outputPath, JsonSerializer.Serialize(report, options), Encoding.UTF8);
		return outputPath;
	}

	/// <summary>
	/// Derives a sibling report path next to the main plan output, stripping a trailing
	/// ".exchanges" segment (if present) so sibling reports read as "<name>.sources.json" rather
	/// than "<name>.exchanges.sources.json".
	/// </summary>
	internal static string DeriveSiblingOutputPath(string planOutputPath, string suffixWithExtension) {
		string directory = Path.GetDirectoryName(planOutputPath) ?? string.Empty;
		string fileName = Path.GetFileName(planOutputPath);
		string baseName = fileName.EndsWith(".exchanges.json", StringComparison.OrdinalIgnoreCase)
			? fileName[..^".exchanges.json".Length]
			: Path.GetFileNameWithoutExtension(fileName);

		return Path.Combine(directory, baseName + suffixWithExtension);
	}
}
