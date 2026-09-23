namespace Analysis;

using System.Text.Json;

internal static class JsonLeaves {

	/// <summary>Every string/number/boolean value in the document with its JSON path ("$.a.b[0]"). Nulls are skipped.</summary>
	public static IEnumerable<(string Path, string Value)> Enumerate(JsonElement element, string path = "$") {
		switch (element.ValueKind) {
			case JsonValueKind.Object:
				foreach (var property in element.EnumerateObject())
					foreach (var leaf in Enumerate(property.Value, $"{path}.{property.Name}"))
						yield return leaf;
				break;
			case JsonValueKind.Array:
				int index = 0;
				foreach (var item in element.EnumerateArray())
					foreach (var leaf in Enumerate(item, $"{path}[{index++}]"))
						yield return leaf;
				break;
			case JsonValueKind.String:
				yield return (path, element.GetString() ?? string.Empty);
				break;
			case JsonValueKind.Number:
			case JsonValueKind.True:
			case JsonValueKind.False:
				yield return (path, element.ToString());
				break;
		}
	}
}
