namespace Analysis;

using Saz;

/// <summary>Plain-language hints for reproducing a captured request and verifying its response.</summary>
internal static class ReplayHints {

	public static List<string> ForRequest(Request request) {
		var dynamicHeaders = request.Headers.Keys.Where(DynamicHeaders.IsDynamic).ToList();
		var body = request.Body;
		var steps = new List<string> {
			$"Create {request.Method} request using HTTP version {request.Version}.",
			"Set URL from host + target path and include query parameters.",
			"Apply static headers captured from the original exchange.",
		};

		if (dynamicHeaders.Count > 0)
			steps.Add("Generate or refresh dynamic headers: " + string.Join(", ", dynamicHeaders) + ".");

		if (request.Headers.ContainsKey("Authorization"))
			steps.Add("Replace Authorization header with a valid token/credential at runtime.");

		if (body.Length > 0) {
			steps.Add($"Build request body as {body.Format} with Content-Type '{body.ContentType}'.");
			if (body.SchemaHints.Count > 0)
				steps.Add("Populate body fields: " + string.Join(", ", body.SchemaHints) + ".");
		}

		return steps;
	}

	public static List<string> ForResponse(Response response) {
		var body = response.Body;
		var steps = new List<string> {
			$"Expect HTTP status code {response.StatusCode} ({response.ReasonPhrase}).",
			"Validate key response headers.",
		};

		if (response.Headers.ContainsKey("Set-Cookie"))
			steps.Add("Capture Set-Cookie values for follow-up requests.");

		if (body.Length > 0) {
			steps.Add($"Validate response body format as {body.Format}.");
			if (body.SchemaHints.Count > 0)
				steps.Add("Verify response fields: " + string.Join(", ", body.SchemaHints) + ".");
		}

		return steps;
	}
}
