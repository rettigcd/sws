namespace Analysis;

using System.Text.Json.Serialization;

internal enum InputKind {
	PathSegment,
	QueryParameter,
	JsonBodyField,
	FormField,
	Cookie,
	RequestHeader,
}

/// <summary>Where an input of a request came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum InputLabel {
	/// <summary>A previous exchange's response supplied the value.</summary>
	Exchange,
	/// <summary>No exchange supplied it; it is a literal, a route word, or user data known ahead of time.</summary>
	PreKnownConstant,
	/// <summary>A header the browser adds on its own (User-Agent, Accept, Sec-Fetch-*, ...).</summary>
	BrowserDefault,
	/// <summary>Looks like an ID/token but no exchange in the capture supplied it.</summary>
	Unknown,
}

/// <summary>One value the request carries: a path segment, query parameter, body field, cookie or header.</summary>
internal sealed record FlowInput(
	[property: JsonConverter(typeof(JsonStringEnumConverter))] InputKind Kind,
	string Name,
	string Value
);

/// <summary>The place in a previous exchange's response that carried a value.</summary>
internal sealed record SourceLocation(
	int ExchangeId,
	int ExchangeIndex,
	string Where
);

internal sealed record InputTrace(
	[property: JsonConverter(typeof(JsonStringEnumConverter))] InputKind Kind,
	string Name,
	string Value,
	InputLabel Label,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SourceLocation? Source,
	// Later exchanges whose responses also carried the value.
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<int>? AlsoSuppliedBy,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note
);

/// <summary>A piece of an exchange's response that a later exchange used.</summary>
internal sealed record OutputUse(
	string Where,
	string Value,
	List<string> ConsumedBy
);

internal sealed record ExchangeTrace(
	int ExchangeId,
	int ExchangeIndex,
	string Method,
	string Url,
	int StatusCode,
	bool Participates,
	// "anchor" (the submission), "source" (supplied a value that was used), or "none".
	string Role,
	List<InputTrace> Inputs,
	List<OutputUse> Outputs
);

internal sealed record AnchorTrace(
	int AnchorExchangeId,
	bool IsLatest,
	List<ExchangeTrace> Exchanges
);

internal sealed record FlowTraceReport(
	string SourceBasePath,
	List<AnchorTrace> Traces
);
