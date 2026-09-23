namespace SazJson;

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Saz;

/// <summary>Writes a <see cref="Session"/> as JSON that a human can read.</summary>
internal static class SessionJsonWriter {

	public static string ToJson(Session session, SessionJsonOptions options) {
		var exchanges = options.IncludeMetadata
			? session.Exchanges
			: [.. session.Exchanges.Select(exchange => exchange with { Metadata = null })];

		var globalHeaders = GlobalHeaders.Find(exchanges);
		var document = new SessionJson(
			session.SourceFile,
			DateTimeOffset.UtcNow,
			globalHeaders,
			GlobalHeaders.Remove(exchanges, globalHeaders.Headers)
		);

		return JsonSerializer.Serialize(document, new JsonSerializerOptions {
			WriteIndented = options.Pretty,
			IndentCharacter = '\t',
			IndentSize = 1,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		});
	}

	public static void Write(string outputPath, Session session, SessionJsonOptions options) {
		File.WriteAllText(outputPath, ToJson(session, options), Encoding.UTF8);
	}
}
