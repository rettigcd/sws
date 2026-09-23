namespace Analysis;

/// <summary>Decides which of the four labels an input gets, given whether an earlier response supplied it.</summary>
internal static class InputClassifier {

	public static (InputLabel Label, string? Note) Classify(FlowInput input, bool hasSource) {
		if (IsBrowserDefault(input))
			return (InputLabel.BrowserDefault, null);

		if (hasSource)
			return (InputLabel.Exchange, null);

		if (input.Kind == InputKind.RequestHeader && IsPageUrlHeader(input.Name))
			return (InputLabel.PreKnownConstant, "URL of the page the request was made from");

		if (SourceLocator.IsSearchable(input) && !IsUserField(input) && SourceLocator.LooksLikeIdentifier(input.Value))
			return (InputLabel.Unknown, "looks like an ID/token but no earlier response in the capture supplied it");

		return (InputLabel.PreKnownConstant, IsUserField(input) ? "user-supplied" : null);
	}

	public static bool IsBrowserDefault(FlowInput input) {
		return input.Kind == InputKind.RequestHeader && ChromeHeadersEngine.IsEngineManagedHeader(input.Name);
	}

	/// <summary>False for inputs no earlier response can meaningfully have supplied: browser-added headers and page-URL headers (Origin/Referer).</summary>
	public static bool ShouldSearchForSource(FlowInput input) {
		return !IsBrowserDefault(input) && !(input.Kind == InputKind.RequestHeader && IsPageUrlHeader(input.Name));
	}

	static bool IsPageUrlHeader(string headerName) {
		return headerName.Equals("Referer", StringComparison.OrdinalIgnoreCase)
			|| headerName.Equals("Origin", StringComparison.OrdinalIgnoreCase);
	}

	static bool IsUserField(FlowInput input) {
		if (input.Kind is not (InputKind.JsonBodyField or InputKind.FormField or InputKind.QueryParameter))
			return false;

		string fieldName = input.Name.Split('.', '[')[^1].TrimEnd(']');
		return SubmissionFinder.IsPersonField(fieldName)
			|| fieldName.Contains("group", StringComparison.OrdinalIgnoreCase);
	}
}
