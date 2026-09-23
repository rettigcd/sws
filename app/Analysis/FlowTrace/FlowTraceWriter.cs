namespace Analysis;

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class FlowTraceWriter {

	const int MaxCellLength = 80;

	/// <summary>Writes "&lt;name&gt;.trace.json" and "&lt;name&gt;.trace.md" next to the main output; returns both paths.</summary>
	public static (string JsonPath, string MarkdownPath) Write(string outputBasePath, FlowTraceReport report) {
		string jsonPath = SourceReportWriter.DeriveSiblingOutputPath(outputBasePath, ".trace.json");
		string markdownPath = SourceReportWriter.DeriveSiblingOutputPath(outputBasePath, ".trace.md");

		File.WriteAllText(jsonPath, ToJson(report), Encoding.UTF8);
		File.WriteAllText(markdownPath, ToMarkdown(report), Encoding.UTF8);
		return (jsonPath, markdownPath);
	}

	public static string ToJson(FlowTraceReport report) {
		return JsonSerializer.Serialize(report, new JsonSerializerOptions {
			WriteIndented = true,
			IndentCharacter = '\t',
			IndentSize = 1,
			DefaultIgnoreCondition = JsonIgnoreCondition.Never,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		});
	}

	public static string ToMarkdown(FlowTraceReport report) {
		var markdown = new StringBuilder();
		markdown.AppendLine($"# Flow trace: {Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(report.SourceBasePath))}");
		markdown.AppendLine();

		if (report.Traces.Count == 0) {
			markdown.AppendLine("No form submissions (POST with name/email/phone fields) were found.");
			return markdown.ToString();
		}

		markdown.AppendLine($"Form submissions found: {string.Join(", ", report.Traces.Select(t => t.AnchorExchangeId))} (latest: {report.Traces[^1].AnchorExchangeId}).");
		foreach (var trace in report.Traces)
			AppendTrace(markdown, trace);

		return markdown.ToString();
	}

	static void AppendTrace(StringBuilder markdown, AnchorTrace trace) {
		var participants = trace.Exchanges.Where(e => e.Participates).ToList();
		var others = trace.Exchanges.Where(e => !e.Participates).ToList();
		var anchor = participants.Single(e => e.Role == "anchor");

		markdown.AppendLine();
		markdown.AppendLine($"## Submission {trace.AnchorExchangeId}{(trace.IsLatest ? " (latest)" : "")}: {anchor.Method} {anchor.Url} -> {anchor.StatusCode}");
		markdown.AppendLine();
		markdown.AppendLine($"Participating exchanges ({participants.Count}): {string.Join(", ", participants.Select(e => e.ExchangeId))}");

		foreach (var exchange in participants) {
			markdown.AppendLine();
			markdown.AppendLine($"### Exchange {exchange.ExchangeId} (index {exchange.ExchangeIndex}, {exchange.Role}): {exchange.Method} {exchange.Url} -> {exchange.StatusCode}");
			AppendInputs(markdown, exchange);
			AppendOutputs(markdown, exchange);
		}

		AppendNonParticipating(markdown, others);
	}

	static void AppendInputs(StringBuilder markdown, ExchangeTrace exchange) {
		markdown.AppendLine();
		markdown.AppendLine("Inputs:");
		markdown.AppendLine();
		markdown.AppendLine("| Kind | Name | Value | Source | Where | Also supplied by | Note |");
		markdown.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
		foreach (var input in exchange.Inputs) {
			string source = input.Label == InputLabel.Exchange
				? $"exchange {input.Source!.ExchangeId} (index {input.Source.ExchangeIndex})"
				: input.Label switch {
					InputLabel.PreKnownConstant => "pre-known constant",
					InputLabel.BrowserDefault => "browser default",
					_ => "UNKNOWN",
				};
			string alsoSuppliedBy = input.AlsoSuppliedBy is null ? "" : string.Join(", ", input.AlsoSuppliedBy);
			markdown.AppendLine($"| {input.Kind} | {Cell(input.Name)} | {Cell(input.Value)} | {source} | {Cell(input.Source?.Where)} | {alsoSuppliedBy} | {Cell(input.Note)} |");
		}
	}

	static void AppendOutputs(StringBuilder markdown, ExchangeTrace exchange) {
		markdown.AppendLine();
		if (exchange.Outputs.Count == 0) {
			markdown.AppendLine("Outputs: none used later.");
			return;
		}

		markdown.AppendLine("Outputs used later:");
		markdown.AppendLine();
		markdown.AppendLine("| Where | Value | Consumed by |");
		markdown.AppendLine("| --- | --- | --- |");
		foreach (var output in exchange.Outputs)
			markdown.AppendLine($"| {Cell(output.Where)} | {Cell(output.Value)} | {Cell(string.Join("; ", output.ConsumedBy))} |");
	}

	static void AppendNonParticipating(StringBuilder markdown, List<ExchangeTrace> others) {
		markdown.AppendLine();
		markdown.AppendLine($"### Not participating ({others.Count})");
		markdown.AppendLine();
		markdown.AppendLine("| Request pattern | Count | Exchange ids |");
		markdown.AppendLine("| --- | --- | --- |");
		foreach (var group in others.GroupBy(PatternOf).OrderBy(g => g.Min(e => e.ExchangeIndex)))
			markdown.AppendLine($"| {Cell(group.Key, int.MaxValue)} | {group.Count()} | {string.Join(", ", group.Select(e => e.ExchangeId))} |");
	}

	/// <summary>"METHOD host/path" with ID/token-like path segments and the query string replaced by placeholders.</summary>
	static string PatternOf(ExchangeTrace exchange) {
		if (!Uri.TryCreate(exchange.Url, UriKind.Absolute, out var uri))
			return $"{exchange.Method} {exchange.Url}";

		var segments = uri.AbsolutePath
			.Split('/', StringSplitOptions.RemoveEmptyEntries)
			.Select(segment => SourceLocator.LooksLikeIdentifier(Uri.UnescapeDataString(segment)) ? "{id}" : segment);
		return $"{exchange.Method} {uri.Host}/{string.Join('/', segments)}";
	}

	static string Cell(string? value, int maxLength = MaxCellLength) {
		if (string.IsNullOrEmpty(value))
			return "";

		string singleLine = value.Replace("\r", " ").Replace("\n", " ").Replace("|", @"\|");
		return singleLine.Length <= maxLength ? singleLine : singleLine[..(maxLength - 1)] + "…";
	}
}
