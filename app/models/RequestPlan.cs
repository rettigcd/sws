using System.Dynamic;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

internal sealed class RequestPlan {

	public string Method { get; }
	public Dictionary<string, string>? QueryParameters { get; }
	public string Url => BuildRequestUri();
	public string NonQueryUrlFormat { get; }

	[JsonIgnore]
	public Dictionary<string,Replacement> Replacements { get; } = new();
	[JsonPropertyName("Replacements")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public Dictionary<string, Replacement>? ReplacementsForJson => Replacements.Count == 0 ? null : Replacements;

	public Version Version { get; }
	public IReadOnlyDictionary<string, string> Headers { get; }
	public IReadOnlyDictionary<string, string> Cookies { get; }
	public Body Body { get; }
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public JsonElement? JsonBody { get; }
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public IReadOnlyList<FormBodyEntry>? FormBody { get; }

	public RequestPlan(Request request) {
		Method = request.Method;
		NonQueryUrlFormat = BuildNonQueryUrlFormat(GetNonQueryUrl(request.Url));
		QueryParameters = BuildQueryParameterPlaceholders(request.QueryParameters);

		Version = ParseHttpVersion(request.Version);
		Headers = ChromeHeadersEngine.BuildHeaderOverrides(request.Headers);
		Cookies = new Dictionary<string, string>(request.Cookies, StringComparer.OrdinalIgnoreCase);
		Body = request.Body;
		JsonBody = request.JsonBody is JsonElement jsonBody
			? BuildJsonBodyPlaceholders(jsonBody)
			: null;
		FormBody = request.FormBody is null
			? null
			: BuildFormBodyPlaceholders(request.FormBody);
	}

	public Task<HttpResponseMessage> Execute(HttpClient? client = null, CancellationToken cancellationToken = default) {
		return ExecuteInternal(client, cancellationToken);
	}

	public Task<HttpResponseMessage> Execute(
		RequestExecutionContext context,
		bool seedCapturedCookies = true,
		CancellationToken cancellationToken = default
	) {
		return ExecuteWithCookieStoreInternal(context, seedCapturedCookies, cancellationToken);
	}

	async Task<HttpResponseMessage> ExecuteInternal(HttpClient? client, CancellationToken cancellationToken) {
		bool ownsClient = client is null;
		client ??= new HttpClient();

		try {
			using var message = BuildHttpRequestMessage(includeManualCookies: true);
			return await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
		}
		finally {
			if (ownsClient)
				client.Dispose();
		}
	}

	async Task<HttpResponseMessage> ExecuteWithCookieStoreInternal(
		RequestExecutionContext context,
		bool seedCapturedCookies,
		CancellationToken cancellationToken
	) {
		ArgumentNullException.ThrowIfNull(context);

		using var message = BuildHttpRequestMessage(includeManualCookies: false);
		if (seedCapturedCookies)
			SeedCapturedCookies(context.CookieStore, message.RequestUri);

		return await context.Client.SendAsync(message, cancellationToken).ConfigureAwait(false);
	}

	HttpRequestMessage BuildHttpRequestMessage(bool includeManualCookies) {
		var message = new HttpRequestMessage(new HttpMethod(Method), BuildRequestUri()) {
			Version = Version,
		};

		var content = BuildHttpContent();
		if (content is not null)
			message.Content = content;

		ApplyHeaders(message);
		if (includeManualCookies)
			ApplyCookies(message);

		return message;
	}

	void SeedCapturedCookies(CookieContainer cookieStore, Uri? requestUri) {
		if (requestUri is null || Cookies.Count == 0)
			return;

		foreach (var cookie in Cookies) {
			try {
				cookieStore.Add(requestUri, new Cookie(cookie.Key, cookie.Value));
			}
			catch (CookieException) {
				// Skip malformed captured cookies while still allowing valid ones.
			}
		}
	}

	HttpContent? BuildHttpContent() {
		if (FormBody is { Count: > 0 }) {
			var resolvedFormBody = FormBody
				.Select(entry => new FormBodyEntry(entry.Key, ResolvePlaceholders(entry.Value)))
				.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
			return new FormUrlEncodedContent(resolvedFormBody);
		}

		if (JsonBody is JsonElement jsonBody) {
			var resolvedJson = ResolveJsonBodyPlaceholders(jsonBody);
			var json = JsonSerializer.Serialize(resolvedJson);
			return new StringContent(json, Encoding.UTF8, "application/json");
		}

		return null;
	}

	void ApplyHeaders(HttpRequestMessage message) {
		SetDefaultHostHeader(message);

		var generatedHeaders = ChromeHeadersEngine.BuildHeaders(Method, message.RequestUri);
		foreach (var generatedHeader in generatedHeaders)
			SetHeader(message, generatedHeader.Key, generatedHeader.Value);

		foreach (var header in Headers) {
			SetHeader(message, header.Key, header.Value);
		}
	}

	static void SetDefaultHostHeader(HttpRequestMessage message) {
		var requestUri = message.RequestUri;
		if (requestUri is null)
			return;

		var host = requestUri.IdnHost;
		if (requestUri.HostNameType == UriHostNameType.IPv6)
			host = $"[{host}]";

		message.Headers.Host = requestUri.IsDefaultPort
			? host
			: $"{host}:{requestUri.Port}";
	}

	static void SetHeader(HttpRequestMessage message, string key, string value) {
		if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase)) {
			message.Headers.Host = value;
			return;
		}

		if (string.Equals(key, "Cookie", StringComparison.OrdinalIgnoreCase))
			return;

		if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase)) {
			if (message.Content is not null && MediaTypeHeaderValue.TryParse(value, out var mediaType))
				message.Content.Headers.ContentType = mediaType;
			return;
		}

		message.Headers.Remove(key);
		message.Content?.Headers.Remove(key);

		if (!message.Headers.TryAddWithoutValidation(key, value))
			message.Content?.Headers.TryAddWithoutValidation(key, value);
	}

	void ApplyCookies(HttpRequestMessage message) {
		if (Cookies.Count == 0)
			return;

		var cookieHeader = string.Join("; ", Cookies.Select(cookie => $"{cookie.Key}={cookie.Value}"));
		message.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
	}

	string BuildNonQueryUrlFormat(string value) {
		int placeholderIndex = 0;
		var parts = value.Split('/', StringSplitOptions.None);

		for (var i = 0; i < parts.Length; i++) {
			var part = parts[i];
			if (!InterestingFinder.IsInteresting(part))
				continue;

			var placeholder = $"{{P{placeholderIndex.ToString(CultureInfo.InvariantCulture)}}}";
			placeholderIndex++;
			Replacements[placeholder] = new Replacement {
				OriginalValue = part,
				Placeholder = placeholder,
			};
			parts[i] = placeholder;
		}

		return string.Join('/', parts);
	}

	Dictionary<string, string> BuildQueryParameterPlaceholders(IReadOnlyDictionary<string, string> queryParameters) {
		Dictionary<string, string> normalized = new(queryParameters.Count, StringComparer.OrdinalIgnoreCase);

		foreach (var pair in queryParameters) {
			if (!InterestingFinder.IsInteresting(pair.Value)) {
				normalized[pair.Key] = pair.Value;
				continue;
			}

			var placeholder = CreateReplacementPlaceholder(pair.Key, pair.Value);
			normalized[pair.Key] = placeholder;
		}

		return normalized;
	}

	List<FormBodyEntry> BuildFormBodyPlaceholders(IReadOnlyList<FormBodyEntry> formBody) {
		var normalized = new List<FormBodyEntry>(formBody.Count);

		foreach (var entry in formBody) {
			if (!InterestingFinder.IsInteresting(entry.Value)) {
				normalized.Add(new FormBodyEntry(entry.Key, entry.Value));
				continue;
			}

			var placeholder = CreateReplacementPlaceholder(entry.Key, entry.Value);
			normalized.Add(new FormBodyEntry(entry.Key, placeholder));
		}

		return normalized;
	}

	JsonElement BuildJsonBodyPlaceholders(JsonElement jsonBody) {
		var node = JsonNode.Parse(jsonBody.GetRawText());
		if (node is null)
			return jsonBody.Clone();

		var withPlaceholders = ReplaceInterestingJsonValues(node, "body");
		return JsonNodeToElement(withPlaceholders);
	}

	JsonElement ResolveJsonBodyPlaceholders(JsonElement jsonBody) {
		var node = JsonNode.Parse(jsonBody.GetRawText());
		if (node is null)
			return jsonBody;

		var resolved = ReplaceJsonValues(node, ResolvePlaceholders);
		return JsonNodeToElement(resolved);
	}

	JsonNode ReplaceInterestingJsonValues(JsonNode node, string currentName) {
		switch (node) {
			case JsonObject obj: {
				var clone = new JsonObject();
				foreach (var property in obj) {
					if (property.Value is null) {
						clone[property.Key] = null;
						continue;
					}

					clone[property.Key] = ReplaceInterestingJsonValues(property.Value, property.Key);
				}

				return clone;
			}
			case JsonArray array: {
				var clone = new JsonArray();
				foreach (var item in array) {
					clone.Add(item is null ? null : ReplaceInterestingJsonValues(item, currentName));
				}

				return clone;
			}
			case JsonValue value:
				if (value.TryGetValue<string>(out var stringValue) && InterestingFinder.IsInteresting(stringValue)) {
					var placeholder = CreateReplacementPlaceholder(currentName, stringValue);
					return JsonValue.Create(placeholder)!;
				}

				return value.DeepClone();
			default:
				return node.DeepClone();
		}
	}

	JsonNode ReplaceJsonValues(JsonNode node, Func<string, string> transform) {
		switch (node) {
			case JsonObject obj: {
				var clone = new JsonObject();
				foreach (var property in obj) {
					if (property.Value is null) {
						clone[property.Key] = null;
						continue;
					}

					clone[property.Key] = ReplaceJsonValues(property.Value, transform);
				}

				return clone;
			}
			case JsonArray array: {
				var clone = new JsonArray();
				foreach (var item in array) {
					clone.Add(item is null ? null : ReplaceJsonValues(item, transform));
				}

				return clone;
			}
			case JsonValue value:
				if (value.TryGetValue<string>(out var stringValue))
					return JsonValue.Create(transform(stringValue))!;

				return value.DeepClone();
			default:
				return node.DeepClone();
		}
	}

	string CreateReplacementPlaceholder(string name, string value) {
		var normalizedName = string.IsNullOrWhiteSpace(name) ? "value" : name;
		var placeholder = $"{{{normalizedName}}}";
		var collisionIndex = 1;

		while (Replacements.ContainsKey(placeholder)) {
			placeholder = $"{{{normalizedName}_{collisionIndex.ToString(CultureInfo.InvariantCulture)}}}";
			collisionIndex++;
		}

		Replacements[placeholder] = new Replacement {
			OriginalValue = value,
			Placeholder = placeholder,
		};

		return placeholder;
	}

	static JsonElement JsonNodeToElement(JsonNode node) {
		using var document = JsonDocument.Parse(node.ToJsonString());
		return document.RootElement.Clone();
	}

	string ResolvePlaceholders(string value) {
		var resolved = value;
		foreach (var replacement in Replacements.Values)
			resolved = resolved.Replace(replacement.Placeholder, replacement.GetValue(), StringComparison.Ordinal);

		return resolved;
	}

	string BuildRequestUri() {
		var baseUrl = NonQueryUrlFormat;
		foreach (var replacement in Replacements.Values)
			baseUrl = baseUrl.Replace(replacement.Placeholder, replacement.GetValue(), StringComparison.Ordinal);

		if (QueryParameters is null || QueryParameters.Count == 0)
			return baseUrl;

		var query = QueryParameters
			.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(ResolvePlaceholders(pair.Value))}")
			.Join("&");

		return baseUrl.Contains('?', StringComparison.Ordinal)
			? $"{baseUrl}&{query}"
			: $"{baseUrl}?{query}";
	}

	static string GetNonQueryUrl(string url) {
		var queryStart = url.IndexOf('?', StringComparison.Ordinal);
		if (queryStart < 0)
			return url;

		return url[..queryStart];
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
