namespace Automation;

/// <summary>Accumulates AutomationSteps during handler execution.</summary>
internal sealed class AutomationStepLog {
	readonly List<AutomationStep> _steps = [];

	public void Record(string description, bool success = true, int? httpStatusCode = null, string? requestUrl = null) {
		_steps.Add(new AutomationStep(
			_steps.Count + 1,
			description,
			success,
			DateTimeOffset.UtcNow,
			httpStatusCode,
			requestUrl is null ? null : StripQuery(requestUrl)
		));
	}

	public List<AutomationStep> ToList() => [.. _steps];

	static string StripQuery(string url) {
		return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : url;
	}
}
