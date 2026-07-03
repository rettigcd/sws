namespace sws.Tests;

using System.Net;
using Automation;

/// <summary>
/// Scripted IAuthHttpClient for tests. Queue expectations in order; each responder receives the
/// actual outgoing request so it can echo engine-generated values (state) or recompute derived
/// values (PKCE code_challenge from a posted code_verifier) to assert protocol correctness.
/// This type must never be referenced outside tests/ - its absence in production code is the
/// automation engine's only safety boundary against hitting a real network.
/// </summary>
sealed class FakeAuthHttpClient : IAuthHttpClient {
	public System.Net.CookieContainer Cookies { get; } = new();
	public List<HttpRequestMessage> SentRequests { get; } = [];

	readonly Queue<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Build)> _queue = new();

	public void Enqueue(HttpMethod method, string urlPrefix, Func<HttpRequestMessage, HttpResponseMessage> build) {
		_queue.Enqueue((
			request => request.Method == method
				&& request.RequestUri is not null
				&& request.RequestUri.GetLeftPart(UriPartial.Path).StartsWith(urlPrefix, StringComparison.OrdinalIgnoreCase),
			build
		));
	}

	public void Enqueue(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> build) {
		_queue.Enqueue((match, build));
	}

	public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) {
		SentRequests.Add(request);

		if (_queue.Count == 0)
			throw new InvalidOperationException($"No scripted response queued for {request.Method} {request.RequestUri}.");

		var (match, build) = _queue.Dequeue();
		if (!match(request))
			throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri} did not match the next queued expectation.");

		return Task.FromResult(build(request));
	}
}

static class FakeResponses {
	public static HttpResponseMessage Html(string html, HttpStatusCode statusCode = HttpStatusCode.OK) {
		return new HttpResponseMessage(statusCode) {
			Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
		};
	}

	public static HttpResponseMessage Json(string json, HttpStatusCode statusCode = HttpStatusCode.OK) {
		return new HttpResponseMessage(statusCode) {
			Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
		};
	}

	public static HttpResponseMessage Redirect(string location, HttpStatusCode statusCode = HttpStatusCode.Found, string? setCookie = null) {
		var response = new HttpResponseMessage(statusCode);
		response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
		if (setCookie is not null)
			response.Headers.Add("Set-Cookie", setCookie);
		return response;
	}
}
