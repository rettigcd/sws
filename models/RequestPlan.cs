using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

internal sealed class RequestPlan {

	public string Method { get; }
	public string Url { get; }
	public Version Version { get; }
	public IReadOnlyDictionary<string, string> Headers { get; }
	public IReadOnlyDictionary<string, string> Cookies { get; }
	public Body Body { get; }
	public JsonElement? JsonBody { get; }
	public IReadOnlyList<FormBodyEntry>? FormBody { get; }

	public RequestPlan(Request request) {
		Method = request.Method;
		Url = request.Url;
		Version = ParseHttpVersion(request.Version);
		Headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
		Cookies = new Dictionary<string, string>(request.Cookies, StringComparer.OrdinalIgnoreCase);
		Body = request.Body;
		JsonBody = request.JsonBody?.Clone();
		FormBody = request.FormBody is null
			? null
			: request.FormBody.Select(entry => new FormBodyEntry(entry.Key, entry.Value)).ToList();
	}

	public Task<HttpResponseMessage> Execute(HttpClient? client = null, CancellationToken cancellationToken = default) {
		return ExecuteInternal(client, cancellationToken);
	}

	async Task<HttpResponseMessage> ExecuteInternal(HttpClient? client, CancellationToken cancellationToken) {
		bool ownsClient = client is null;
		client ??= new HttpClient();

		try {
			using var message = BuildHttpRequestMessage();
			return await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
		}
		finally {
			if (ownsClient)
				client.Dispose();
		}
	}

	HttpRequestMessage BuildHttpRequestMessage() {
		var message = new HttpRequestMessage(new HttpMethod(Method), Url) {
			Version = Version,
		};

		var content = BuildHttpContent();
		if (content is not null)
			message.Content = content;

		ApplyHeaders(message);
		ApplyCookies(message);

		return message;
	}

	HttpContent? BuildHttpContent() {
		if (FormBody is { Count: > 0 })
			return new FormUrlEncodedContent(FormBody.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));

		if (JsonBody is JsonElement jsonBody) {
			var json = JsonSerializer.Serialize(jsonBody);
			return new StringContent(json, Encoding.UTF8, "application/json");
		}

		return null;
	}

	void ApplyHeaders(HttpRequestMessage message) {
		foreach (var header in Headers) {
			if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)) {
				message.Headers.Host = header.Value;
				continue;
			}

			if (string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
				continue;

			if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) {
				if (message.Content is not null && MediaTypeHeaderValue.TryParse(header.Value, out var mediaType))
					message.Content.Headers.ContentType = mediaType;
				continue;
			}

			if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
				message.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
		}
	}

	void ApplyCookies(HttpRequestMessage message) {
		if (Cookies.Count == 0)
			return;

		var cookieHeader = string.Join("; ", Cookies.Select(cookie => $"{cookie.Key}={cookie.Value}"));
		message.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
	}

	static Version ParseHttpVersion(string httpVersion) {
		const string prefix = "HTTP/";
		if (!httpVersion.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			return HttpVersion.Version11;

		var versionText = httpVersion[prefix.Length..];
		if (Version.TryParse(versionText, out var parsedVersion))
			return parsedVersion;

		if (double.TryParse(versionText, NumberStyles.Float, CultureInfo.InvariantCulture, out var asDouble)) {
			var major = (int)Math.Truncate(asDouble);
			var minor = (int)Math.Round((asDouble - major) * 10d);
			return new Version(major, minor);
		}

		return HttpVersion.Version11;
	}
}
