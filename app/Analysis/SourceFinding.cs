namespace Analysis;

internal sealed record SourceFinding(
	int ExchangeId,
	string SourceKind,
	string? SourceName,
	string Needle) {
	public override string ToString() {
		return SourceName is null
			? $"Exchange {ExchangeId} {SourceKind} contained '{Needle}'"
			: $"Exchange {ExchangeId} {SourceKind} '{SourceName}' contained '{Needle}'";
	}
}
