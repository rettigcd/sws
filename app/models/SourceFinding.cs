internal sealed record SourceFinding(
	int SessionId,
	string SourceKind,
	string? SourceName,
	string Needle) {
	public override string ToString() {
		return SourceName is null
			? $"Session {SessionId} {SourceKind} contained '{Needle}'"
			: $"Session {SessionId} {SourceKind} '{SourceName}' contained '{Needle}'";
	}
}
