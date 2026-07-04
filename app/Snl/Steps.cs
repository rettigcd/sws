using AngleSharp;

namespace Snl;

internal static class Steps {

	/// <returns>
	/// <see cref="StepResult.IndexPageRetrieved"/> if the endpoint returned a successful HTML response, populating
	/// <see cref="Context.JavascriptScripts"/> and <see cref="Context.CurrentPage"/>.
	/// </returns>
	public static async Task<StepResult> GetIndexPageAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");

		var requestUri = "https://bookings-us.qudini.com/booking-widget/events/UZJLSRJUNZC/event/choose";
		var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
		request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
		request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
		request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
		request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Google Chrome\";v=\"149\", \"Chromium\";v=\"149\", \"Not)A;Brand\";v=\"24\"");
		request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
		request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
		request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36");

		var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Index page endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		var html = await response.Content.ReadAsStringAsync();

		var browsingContext = BrowsingContext.New(Configuration.Default);
		var document = await browsingContext.OpenAsync(req => req.Content(html).Address(requestUri)).ConfigureAwait(false);
		var baseUri = new Uri(requestUri);

		var scriptUris = document.QuerySelectorAll("script[src]")
			.Select(script => script.GetAttribute("src"))
			.Where(src => !string.IsNullOrWhiteSpace(src))
			.Select(src => new Uri(baseUri, src!).ToString())
			.ToList();

		if (scriptUris.Count == 0)
			throw new InvalidOperationException("Index page did not contain any <script src> tags.");

		ctx.JavascriptScripts.AddRange(scriptUris);
		ctx.CurrentPage = requestUri;

		return StepResult.IndexPageRetrieved;
	}

	/// <returns>
	/// <see cref="StepResult.ScriptRequested"/> once the script resource returns a successful response; the response body is discarded.
	/// </returns>
	public static async Task<StepResult> RequestGenericAsync(Context ctx, string uri) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");

		var request = new HttpRequestMessage(HttpMethod.Get, uri);
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		request.Headers.TryAddWithoutValidation("Accept", "*/*");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "script");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");

		using var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Script endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		return StepResult.ScriptRequested;
	}

}
