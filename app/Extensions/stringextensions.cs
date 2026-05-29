
internal static class StringExtensions {

	extension( IEnumerable<string> values ) {
		// Get Property per value
		// public bool IsBlank => string.IsNullOrWhiteSpace(values);

		public string Join(string glue) => string.Join(glue,values);
	}

}
