using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length == 0) {
	PrintUsage();
	return 1;
}

string inputPath = args[0];
if (!File.Exists(inputPath)) {
	Console.Error.WriteLine($"Input file not found: {inputPath}");
	return 2;
}

if (!string.Equals(Path.GetExtension(inputPath), ".saz", StringComparison.OrdinalIgnoreCase)) {
	Console.Error.WriteLine("Input must be a .saz file.");
	return 2;
}


string? outputPath = null;
bool pretty = true;
SazBuildOptions buildOptions = new ();

for (int i = 1; i < args.Length; i++) {
	string arg = args[i];
	if (arg is "--compact") {
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
		buildOptions.IncludeConnect = true;
	}
	else if (arg is "--include-css") {
		buildOptions.IncludeCss = true;
	}
	else if (arg is "--include-media") {
		buildOptions.IncludeMedia = true;
	}
	else if (arg is "--include-metadata") {
		buildOptions.IncludeMetadata = true;
	}
	else if (arg is "--include-sourcemaps") {
		buildOptions.IncludeSourcemaps = true;
	}
	else if (arg is "--sources-all") {
		// Deprecated: sources report is now always generated.
	}
	else {
		Console.Error.WriteLine($"Unknown argument: {arg}");
		return 2;
	}
}

try {
	var plan = SazPlanBuilder.Build(inputPath, buildOptions);
	var classifiedSessions = Auth.SessionClassifier.ClassifyUnknownSessions(plan.Sessions).ToList();
	var classifiedPlan = plan with { Sessions = classifiedSessions };
	string planOutputPath = ResolveOutputPath(inputPath, outputPath);
	string authOutputPath = SazPlanBuilder.DeriveSiblingOutputPath(planOutputPath, ".auth.json");

	WriteSazPlanAsJson(planOutputPath, pretty, classifiedPlan);
	WriteAuthFlowReportAsJson(authOutputPath, pretty, Auth.AuthFlowDetector.Detect(classifiedSessions));
	SazPlanBuilder.WriteAllSessionSourcesReport(planOutputPath, classifiedSessions);

	return 0;
}
catch (Exception ex) {
	Console.Error.WriteLine($"Failed to build plan: {ex.Message}");
	return 1;
}

static void PrintUsage() {
	Console.WriteLine("Usage:");
	Console.WriteLine("  sws <input.saz> [--out plan.json] [--compact] [--include-connect] [--include-css] [--include-media] [--include-metadata] [--include-sourcemaps]");
}

static string ResolveOutputPath(string inputPath, string? outputPath) {
	if (!string.IsNullOrWhiteSpace(outputPath)) {
		if (Path.IsPathRooted(outputPath))
			return outputPath;

		string inputDirectory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? Directory.GetCurrentDirectory();
		return Path.Combine(inputDirectory, outputPath);
	}

	return Path.ChangeExtension(inputPath, ".sessions.json");
}

static void WriteSazPlanAsJson(string outputPath, bool pretty, Saz plan) {
	string json = JsonSerializer.Serialize(plan, new JsonSerializerOptions {
		WriteIndented = pretty,
		IndentCharacter = '\t',
		IndentSize = 1,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	});

	File.WriteAllText(outputPath, json, Encoding.UTF8);
}

static void WriteAuthFlowReportAsJson(
	string outputPath,
	bool pretty,
	Auth.AuthFlowDetectionResult report
) {
	string json = JsonSerializer.Serialize(report, new JsonSerializerOptions {
		WriteIndented = pretty,
		IndentCharacter = '\t',
		IndentSize = 1,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	});

	File.WriteAllText(outputPath, json, Encoding.UTF8);
}