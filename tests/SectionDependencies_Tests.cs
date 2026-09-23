namespace sws.Tests;

using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

/// <summary>
/// The app is split into sections that must stay independent:
/// Saz (reads a .saz into memory) depends on nothing; SazJson (writes it as JSON) depends only on Saz;
/// everything else analyzes the Saz model and never uses SazJson.
/// </summary>
public class SectionDependencies_Tests {

	static readonly string[] AnalysisSections = ["Analysis", "Auth", "AzureB2c", "Automation", "Minimal", "Qudini"];

	[Fact]
	public void Saz_DependsOnNoOtherSection() {
		AssertNoReferences("Saz", ["SazJson", .. AnalysisSections]);
	}

	[Fact]
	public void SazJson_DependsOnlyOnSaz() {
		AssertNoReferences("SazJson", AnalysisSections);
	}

	[Fact]
	public void AnalysisSections_DoNotUseSazJson() {
		foreach (string section in AnalysisSections)
			AssertNoReferences(section, ["SazJson"]);
	}

	static void AssertNoReferences(string section, IEnumerable<string> forbiddenSections) {
		string sectionDirectory = Path.Combine(FindAppDirectory(), section);
		var offenders = new List<string>();

		foreach (string file in Directory.EnumerateFiles(sectionDirectory, "*.cs", SearchOption.AllDirectories)) {
			string text = File.ReadAllText(file);
			foreach (string forbidden in forbiddenSections) {
				if (Regex.IsMatch(text, $@"^\s*using\s+{forbidden}\s*;", RegexOptions.Multiline)
					|| Regex.IsMatch(text, $@"(?<![\w.]){forbidden}\.[A-Z]"))
					offenders.Add($"{Path.GetFileName(file)} -> {forbidden}");
			}
		}

		offenders.ShouldBeEmpty();
	}

	static string FindAppDirectory() {
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null) {
			string app = Path.Combine(directory.FullName, "app");
			if (File.Exists(Path.Combine(app, "sws.csproj")))
				return app;

			directory = directory.Parent;
		}

		throw new InvalidOperationException("Could not locate the app directory from the test base directory.");
	}
}
