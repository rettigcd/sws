namespace Analysis;

using Saz;

/// <summary>
/// Starting at a submission, works out every request value it needed and which earlier response supplied each one,
/// then does the same for each supplying exchange, until every value is accounted for.
/// </summary>
internal static class FlowTracer {

	/// <summary>Traces every form submission found in the capture; the last one is flagged as the latest.</summary>
	public static FlowTraceReport TraceAll(IReadOnlyList<Exchange> exchanges, string sourceBasePath) {
		var submissions = SubmissionFinder.Find(exchanges);
		var traces = new List<AnchorTrace>();
		for (int i = 0; i < submissions.Count; i++) {
			int anchorIndex = IndexOf(exchanges, submissions[i]);
			traces.Add(Trace(exchanges, anchorIndex, isLatest: i == submissions.Count - 1));
		}

		return new FlowTraceReport(sourceBasePath, traces);
	}

	public static AnchorTrace Trace(IReadOnlyList<Exchange> exchanges, int anchorIndex, bool isLatest) {
		var inputsByIndex = new Dictionary<int, List<InputTrace>>();
		var outputsByIndex = new Dictionary<int, Dictionary<string, OutputUse>>();
		Visit(exchanges, anchorIndex, inputsByIndex, outputsByIndex);

		var traced = new List<ExchangeTrace>(exchanges.Count);
		for (int i = 0; i < exchanges.Count; i++) {
			var exchange = exchanges[i];
			bool participates = inputsByIndex.ContainsKey(i);
			traced.Add(new ExchangeTrace(
				exchange.ExchangeId,
				i,
				exchange.Request.Method,
				exchange.Request.Url,
				exchange.Response.StatusCode,
				participates,
				!participates ? "none" : i == anchorIndex ? "anchor" : "source",
				participates ? inputsByIndex[i] : [],
				outputsByIndex.TryGetValue(i, out var outputs) ? [.. outputs.Values] : []
			));
		}

		return new AnchorTrace(exchanges[anchorIndex].ExchangeId, isLatest, traced);
	}

	static void Visit(
		IReadOnlyList<Exchange> exchanges,
		int index,
		Dictionary<int, List<InputTrace>> inputsByIndex,
		Dictionary<int, Dictionary<string, OutputUse>> outputsByIndex
	) {
		if (inputsByIndex.ContainsKey(index))
			return;

		var inputTraces = new List<InputTrace>();
		inputsByIndex[index] = inputTraces;

		var exchange = exchanges[index];
		foreach (var input in InputExtractor.Extract(exchange)) {
			var sources = InputClassifier.ShouldSearchForSource(input) ? SourceLocator.Find(exchanges, index, input) : [];
			var (label, note) = InputClassifier.Classify(input, sources.Count > 0);

			var first = sources.Count > 0 ? sources[0] : null;
			inputTraces.Add(new InputTrace(
				input.Kind,
				input.Name,
				input.Value,
				label,
				first,
				sources.Count > 1 ? [.. sources.Skip(1).Select(source => source.ExchangeId)] : null,
				note
			));

			if (first is null)
				continue;

			RecordOutput(outputsByIndex, first, input, exchange.ExchangeId);
			Visit(exchanges, first.ExchangeIndex, inputsByIndex, outputsByIndex);
		}
	}

	static void RecordOutput(Dictionary<int, Dictionary<string, OutputUse>> outputsByIndex, SourceLocation source, FlowInput input, int consumerExchangeId) {
		if (!outputsByIndex.TryGetValue(source.ExchangeIndex, out var outputs))
			outputsByIndex[source.ExchangeIndex] = outputs = [];

		string key = $"{source.Where}\n{input.Value}";
		if (!outputs.TryGetValue(key, out var output))
			outputs[key] = output = new OutputUse(source.Where, input.Value, []);

		string consumer = $"{consumerExchangeId} {input.Kind} {input.Name}";
		if (!output.ConsumedBy.Contains(consumer))
			output.ConsumedBy.Add(consumer);
	}

	static int IndexOf(IReadOnlyList<Exchange> exchanges, Exchange exchange) {
		for (int i = 0; i < exchanges.Count; i++)
			if (ReferenceEquals(exchanges[i], exchange))
				return i;

		throw new ArgumentException("Exchange is not in the list.", nameof(exchange));
	}
}
