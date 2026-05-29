class Replacement {
	public required string Placeholder { get; init; }
	public required string OriginalValue { get; init; }

	public object? Source { get; set; }

	public string GetValue(){ 
		return this.OriginalValue;
	}
}