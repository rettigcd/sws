namespace Analysis;

using System.Text.Json;
using System.Text.RegularExpressions;
using Saz;

/// <summary>Finds exchanges where a user submitted a form containing personal details (name, email, phone).</summary>
internal static class SubmissionFinder {

	static readonly Regex PersonFieldPattern = new(
		@"^(first_?|last_?|full_?)?name$|e-?mail|phone|mobile",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	public static bool IsPersonField(string fieldName) => PersonFieldPattern.IsMatch(fieldName);

	/// <summary>Submissions in capture order; the last one is the latest.</summary>
	public static List<Exchange> Find(IReadOnlyList<Exchange> exchanges) {
		return exchanges
			.Where(exchange => !string.Equals(exchange.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
			.Where(exchange => BodyFieldNames(exchange.Request).Count(IsPersonField) >= 2)
			.ToList();
	}

	static IEnumerable<string> BodyFieldNames(Request request) {
		if (request.JsonBody is { ValueKind: JsonValueKind.Object } json)
			return json.EnumerateObject().Select(property => property.Name).ToList();

		if (request.FormBody is not null)
			return request.FormBody.Select(entry => entry.Key).ToList();

		return [];
	}
}
