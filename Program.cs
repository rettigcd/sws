using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length == 0) {
	PrintUsage();
	return 1;
}

var inputPath = args[0];
if (!File.Exists(inputPath)) {
	Console.Error.WriteLine($"Input file not found: {inputPath}");
	return 2;
}

if (!string.Equals(Path.GetExtension(inputPath), ".saz", StringComparison.OrdinalIgnoreCase)) {
	Console.Error.WriteLine("Input must be a .saz file.");
	return 2;
}

string? outputPath = null;
var pretty = true;
var includeConnect = false;
var includeCss = false;
var includeMedia = false;
var includeMetadata = false;
var includeSourcemaps = false;
int? sourcesSessionIndex = null;
var sourcesAll = false;

for (var i = 1; i < args.Length; i++) {
	var arg = args[i];
	if (arg is "--pretty") {
		pretty = true;
	}
	else if (arg is "--compact") {
		pretty = false;
	}
	else if (arg is "--out") {
		if (i + 1 >= args.Length) {
			Console.Error.WriteLine("Missing value for --out");
			return 2;
		}
		outputPath = args[++i];
	}
	else if (arg is "--include-connect") {
		includeConnect = true;
	}
	else if (arg is "--include-css") {
		includeCss = true;
	}
	else if (arg is "--include-media") {
		includeMedia = true;
	}
	else if (arg is "--include-metadata") {
		includeMetadata = true;
	}
	else if (arg is "--include-sourcemaps") {
		includeSourcemaps = true;
	}
	else if (arg is "--sources") {
		if (i + 1 >= args.Length) {
			Console.Error.WriteLine("Missing value for --sources");
			return 2;
		}

		if (!int.TryParse(args[++i], out var parsedSessionIndex) || parsedSessionIndex < 1) {
			Console.Error.WriteLine("--sources requires a session index of 1 or greater");
			return 2;
		}

		sourcesSessionIndex = parsedSessionIndex;
	}
	else if (arg is "--sources-all") {
		sourcesAll = true;
	}
	else {
		Console.Error.WriteLine($"Unknown argument: {arg}");
		return 2;
	}
}

try {
	var plan = SazPlanBuilder.Build(inputPath, includeConnect, includeCss, includeMedia, includeMetadata, includeSourcemaps);
	var options = new JsonSerializerOptions {
		WriteIndented = pretty,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
	var json = JsonSerializer.Serialize(plan, options);

	if (string.IsNullOrWhiteSpace(outputPath)) {
		Console.WriteLine(json);
	}
	else {
		File.WriteAllText(outputPath, json, Encoding.UTF8);
		Console.WriteLine($"Plan written: {outputPath}");
	}

	if (sourcesAll && sourcesSessionIndex is not null) {
		Console.Error.WriteLine("Use either --sources or --sources-all, not both.");
		return 2;
	}

	if (sourcesAll) {
		var sourcesBasePath = outputPath ?? inputPath;
		var sourcesPath = SazPlanBuilder.WriteAllSessionSourcesReport(sourcesBasePath, plan.Sessions);
		Console.WriteLine($"Sources written: {sourcesPath}");
	}
	else if (sourcesSessionIndex is not null) {
		var sourcesBasePath = outputPath ?? inputPath;
		var sourcesPath = SazPlanBuilder.WriteSessionSourcesReport(sourcesBasePath, sourcesSessionIndex.Value, plan.Sessions);
		Console.WriteLine($"Sources written: {sourcesPath}");
	}

	Console.WriteLine($"Sessions planned: {plan.Sessions.Count}");
	return 0;
}
catch (Exception ex) {
	Console.Error.WriteLine($"Failed to build plan: {ex.Message}");
	return 1;
}

static void PrintUsage() {
	Console.WriteLine("Usage:");
	Console.WriteLine("  sws <input.saz> [--out plan.json] [--pretty|--compact] [--include-connect] [--include-css] [--include-media] [--include-metadata] [--include-sourcemaps] [--sources <sessionIndex>] [--sources-all]");
}
