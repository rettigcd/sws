using System.Net;

namespace Automation;

/// <summary>
/// Mockable HTTP transport for the automation engine. Tests use a scripted fake; production
/// code uses SystemNetAuthHttpClient. This is the only safety boundary the engine has -
/// there is no environment allowlist, so tests must never wire up the real implementation.
/// </summary>
public interface IAuthHttpClient {
	Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);

	CookieContainer Cookies { get; }
}

/// <summary>
/// Real HttpClient-backed implementation. AllowAutoRedirect is disabled so the engine can
/// inspect and follow (or terminate on) each redirect itself per the detected flow's rules.
/// </summary>
internal sealed class SystemNetAuthHttpClient : IAuthHttpClient, IDisposable {
	readonly HttpClientHandler _handler;
	readonly HttpClient _client;

	public CookieContainer Cookies { get; }

	public SystemNetAuthHttpClient(TimeSpan? timeout = null) {
		Cookies = new CookieContainer();
		_handler = new HttpClientHandler {
			UseCookies = true,
			CookieContainer = Cookies,
			AllowAutoRedirect = false,
		};
		_client = new HttpClient(_handler, disposeHandler: false) {
			Timeout = timeout ?? TimeSpan.FromSeconds(30),
		};
	}

	public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) {
		return _client.SendAsync(request, cancellationToken);
	}

	public void Dispose() {
		_client.Dispose();
		_handler.Dispose();
	}
}
