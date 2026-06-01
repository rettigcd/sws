internal static class ReplacementSourceResolver {
	static readonly HashSet<string> AzureB2cFlowValueKeys =
	[
		"client_id",
		"redirect_uri",
		"code_challenge",
		"code_verifier",
		"nonce",
		"state",
		"grant_type",
		"response_type",
		"response_mode",
		"scope",
		"prompt",
		"login_hint",
		"client-request-id",
	];

	public static void PopulateReplacementSources(
		RequestPlan requestPlan,
		IReadOnlyList<Session> previousSessions,
		Dictionary<string, string>? missing,
		bool isAzureB2cFlowSession,
		Func<IReadOnlyList<Session>, string, List<SourceFinding>> getOrderedSources,
		Func<Dictionary<string, string>, string, string, string> registerMissingValue
	) {
		foreach (var replacement in requestPlan.Replacements.Values) {
			var orderedSources = getOrderedSources(previousSessions, replacement.OriginalValue);
			var source = orderedSources.FirstOrDefault();
			if (source is not null) {
				replacement.Source = source;
				continue;
			}

			if (isAzureB2cFlowSession && TryBuildAzureB2cSourceReference(replacement.Placeholder, out var azureB2cSource)) {
				replacement.Source = azureB2cSource;
				continue;
			}

			var missingKey = replacement.Placeholder;
			if (missing is not null)
				missingKey = registerMissingValue(missing, replacement.Placeholder, replacement.OriginalValue);

			replacement.Source = new MissingSourceReference(missingKey);
		}
	}

	static bool TryBuildAzureB2cSourceReference(string placeholder, out string sourceReference) {
		sourceReference = string.Empty;
		if (string.IsNullOrWhiteSpace(placeholder))
			return false;

		var trimmed = placeholder.Trim();
		if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal) && trimmed.Length > 2)
			trimmed = trimmed[1..^1];

		if (trimmed.Length == 0)
			return false;

		var key = StripNumericSuffix(trimmed);
		if (!AzureB2cFlowValueKeys.Contains(key))
			return false;

		sourceReference = $"{{AzureB2C:{key}}}";
		return true;
	}

	static string StripNumericSuffix(string value) {
		var lastUnderscore = value.LastIndexOf('_');
		if (lastUnderscore <= 0 || lastUnderscore >= value.Length - 1)
			return value;

		for (var i = lastUnderscore + 1; i < value.Length; i++)
			if (!char.IsDigit(value[i]))
				return value;

		return value[..lastUnderscore];
	}
}
