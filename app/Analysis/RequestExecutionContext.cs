using System.Net;

internal sealed class RequestExecutionContext : IDisposable {
	readonly HttpClientHandler _handler;

	public CookieContainer CookieStore { get; }
	public HttpClient Client { get; }

	public RequestExecutionContext(CookieContainer? cookieStore = null) {
		CookieStore = cookieStore ?? new CookieContainer();
		_handler = new HttpClientHandler {
			UseCookies = true,
			CookieContainer = CookieStore,
		};

		Client = new HttpClient(_handler, disposeHandler: false);
	}

	public void Dispose() {
		Client.Dispose();
		_handler.Dispose();
	}
}