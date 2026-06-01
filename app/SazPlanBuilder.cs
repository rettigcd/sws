using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class SazPlanBuilder {
	private static readonly object MalformedMetadataLogLock = new();
	private static readonly string MalformedMetadataLogPath = Path.Combine(Environment.CurrentDirectory, "malformed-metadata.log");
	private static readonly HashSet<string> MediaPathExtensions =
	[
		".png",
		".jpg",
		".jpeg",
		".gif",
		".webp",
		".bmp",
		".ico",
		".svg",
		".mp4",
		".webm",
		".avi",
		".mov",
		".mp3",
		".wav",
		".ogg",
		".m4a",
		".m4v",
		".woff",
		".woff2",
		".ttf",
		".otf",
		".eot",
	];
	
	static readonly Regex RawFilePattern = new(
		@"^raw\/(?<id>\d+)_(?<kind>[csm])\.(?<ext>txt|xml)$", 
		RegexOptions.Compiled | RegexOptions.IgnoreCase
	);
	
	static readonly HashSet<string> DynamicHeaderNames =
	[
		"content-length",
		"date",
		"x-request-id",
		"traceparent",
		"x-correlation-id",
		"x-amzn-trace-id",
		"x-forwarded-for",
		"x-forwarded-proto",
		"x-real-ip",
	];

	static readonly HashSet<string> ExcludedHosts =
	[
		"svcs.tql.com",
	];

	public static Saz Build(
		string sazPath,
		SazBuildOptions options
	) {
		var map = LoadSessionRawMap(sazPath);

		var sessions = map
			.OrderBy(kvp => kvp.Key)
			.Select(kvp => BuildSessionPlan(kvp.Value, options.IncludeMetadata))
			.Where(session => 
				   (options.IncludeConnect || !IsConnectSession(session)) 
				&& (options.IncludeCss || !IsCssSession(session))
				&& (options.IncludeMedia || !IsMediaSession(session))
				&& (options.IncludeSourcemaps || !IsSourcemapSession(session))
				&& !IsExcludedHostSession(session)
			)
			.ToList();

		var globalHeaders = BuildGlobalHeadersGroup(sessions);
		var sessionsWithoutGlobalHeaders = RemoveGlobalHeaders(sessions, globalHeaders.Headers);

		return new Saz(
			Path.GetFullPath(sazPath),
			DateTimeOffset.UtcNow,
			globalHeaders,
			sessionsWithoutGlobalHeaders
		);
	}

	static Dictionary<int, SessionRaw> LoadSessionRawMap(string sazPath) {
		using var stream = File.OpenRead(sazPath);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

		var map = new Dictionary<int, SessionRaw>();
		foreach (var entry in archive.Entries) {
			var match = RawFilePattern.Match(entry.FullName);
			if (!match.Success)
				continue;

			var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
			var kind = match.Groups["kind"].Value.ToLowerInvariant();

			if (!map.TryGetValue(id, out var sessionRaw)) {
				sessionRaw = new SessionRaw(id);
				map[id] = sessionRaw;
			}

			using var entryStream = entry.Open();
			using var memory = new MemoryStream();
			entryStream.CopyTo(memory);
			var bytes = memory.ToArray();

			switch (kind) {
				case "c":	sessionRaw.ClientRequestBytes = bytes;	break;
				case "s":	sessionRaw.ServerResponseBytes = bytes;	break;
				case "m":	sessionRaw.MetadataBytes = bytes;		break;
			}
		}

		return map;
	}

	public static string WriteAllSessionSourcesReport(string outputBasePath, IReadOnlyList<Session> sessions) {
		var outputPath = Path.ChangeExtension(outputBasePath, ".sources.json");
		var options = new JsonSerializerOptions {
			WriteIndented = true,
			IndentCharacter = '\t',
			IndentSize = 1,
			DefaultIgnoreCondition = JsonIgnoreCondition.Never,
		};

		var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var unsourcedCookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		var report = new SessionSourcesBatchReport(
			Path.GetFullPath(outputBasePath),
			missing,
			unsourcedCookies,
			sessions
				.Select((session, index) => SourceReportBuilder.BuildSessionSourcesReport(index, sessions, missing, unsourcedCookies))
				.ToList()
		);

		File.WriteAllText(outputPath, JsonSerializer.Serialize(report, options), Encoding.UTF8);
		return outputPath;
	}

	static GlobalHeadersGroup BuildGlobalHeadersGroup(List<Session> sessions) {
		if (sessions.Count == 0)
			return new GlobalHeadersGroup("global-headers", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

		var commonHeaders = sessions[0].Request.Headers
			.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);

		foreach (var session in sessions.Skip(1)) {
			var toRemove = commonHeaders
				.Where(commonHeader =>
					!session.Request.Headers.TryGetValue(commonHeader.Key, out var value) ||
					!string.Equals(value, commonHeader.Value, StringComparison.Ordinal))
				.Select(commonHeader => commonHeader.Key)
				.ToList();

			foreach (var key in toRemove)
				commonHeaders.Remove(key);
		}

		return new GlobalHeadersGroup("global-headers", SortHeaders(commonHeaders));
	}


	static List<Session> RemoveGlobalHeaders(
		List<Session> sessions,
		Dictionary<string, string> globalHeaders) {
		if (globalHeaders.Count == 0)
			return sessions;

		return sessions
			.Select(session => {
				var requestHeaders = session.Request.Headers
					.Where(header => !globalHeaders.ContainsKey(header.Key))
					.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);

				var request = session.Request with {
					Headers = SortHeaders(requestHeaders),
				};

				return session with { Request = request };
			})
			.ToList();
	}

	static bool IsConnectSession(Session session) {
		return string.Equals(session.Request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase);
	}

	static bool IsCssSession(Session session) {
		if (HasPathExtension(session.Request.Url, ".css"))
			return true;

		if (session.Response.Headers.TryGetValue("Content-Type", out var responseContentType) &&
			responseContentType.Contains("text/css", StringComparison.OrdinalIgnoreCase)) {
			return true;
		}

		return false;
	}

	static bool IsMediaSession(Session session) {
		if (HasPathExtension(session.Request.Url, MediaPathExtensions))
			return true;

		if (session.Response.Headers.TryGetValue("Content-Type", out var responseContentType)) {
			if (responseContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) 
				|| responseContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) 
				|| responseContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
				|| responseContentType.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
			)
				return true;

			if (responseContentType.Contains("application/font", StringComparison.OrdinalIgnoreCase))
				return true;

		}

		return false;
	}

	static bool IsSourcemapSession(Session session) {
		return HasPathExtension(session.Request.Url, ".map");
	}

	static bool IsExcludedHostSession(Session session) {
		var host = GetNormalizedHost(session);
		return !string.IsNullOrWhiteSpace(host) && ExcludedHosts.Contains(host);
	}

	static string GetNormalizedHost(Session session) {
		if (Uri.TryCreate(session.Request.Url, UriKind.Absolute, out var uri))
			return uri.Host.ToLowerInvariant();

		var host = session.Request.Host?.Trim() ?? string.Empty;
		if (host.Length == 0)
			return string.Empty;

		if (!host.StartsWith("[", StringComparison.Ordinal)) {
			var firstColon = host.IndexOf(":", StringComparison.Ordinal);
			var lastColon = host.LastIndexOf(":", StringComparison.Ordinal);
			if (firstColon > 0 && firstColon == lastColon)
				host = host[..firstColon];
		}

		return host.ToLowerInvariant();
	}

	static bool HasPathExtension(string urlOrPath, string extension) {
		if (string.IsNullOrWhiteSpace(urlOrPath))
			return false;

		if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri))
			return uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

		var path = urlOrPath.Split('?', 2)[0];
		return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
	}

	static bool HasPathExtension(string urlOrPath, HashSet<string> extensions) {
		if (string.IsNullOrWhiteSpace(urlOrPath))
			return false;

		string path;
		if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri))
			path = uri.AbsolutePath;
		else
			path = urlOrPath.Split('?', 2)[0];

		var extension = Path.GetExtension(path);
		if (string.IsNullOrWhiteSpace(extension))
			return false;

		return extensions.Contains(extension.ToLowerInvariant());
	}

	static Session BuildSessionPlan(SessionRaw raw, bool includeMetadata) {
		var metadata = includeMetadata ? ParseMetadata(raw.MetadataBytes, raw.Id) : null;
		var request = ParseHttpMessage(raw.ClientRequestBytes, isRequest: true);
		var response = ParseHttpMessage(raw.ServerResponseBytes, isRequest: false);

		var requestLine = ParseRequestLine(request.StartLine);
		var statusLine = ParseStatusLine(response.StartLine);
		var hostHeader = GetHeader(request.Headers, "Host");

		var url = BuildUrl(requestLine.Method, requestLine.Target, hostHeader, metadata);
		var requestBodyText = BodyAnalyzer.DecodeBodyText(request.Headers, request.BodyBytes);
		var requestBody = BodyAnalyzer.BuildBodyPlan(request.Headers, request.BodyBytes);
		var requestJsonBody = BodyAnalyzer.BuildRequestJsonBody(requestBody.Format, requestBodyText);
		var requestFormBody = BodyAnalyzer.BuildRequestFormBody(requestBody.Format, requestBodyText);
		var responseBodyText = BodyAnalyzer.DecodeBodyText(response.Headers, response.BodyBytes);
		var responseBody = BodyAnalyzer.BuildBodyPlan(response.Headers, response.BodyBytes);
		var responseText = BodyAnalyzer.BuildResponseText(response.Headers, responseBodyText);
		var responseJson = BodyAnalyzer.BuildResponseJson(responseBody.Format, responseBodyText);

		var requestHeaders = request.Headers
			.Where(h => !DynamicHeaderNames.Contains(h.Key.ToLowerInvariant()))
			.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);

		var dynamicHeaders = request.Headers
			.Where(h => DynamicHeaderNames.Contains(h.Key.ToLowerInvariant()))
			.Select(h => h.Key)
			.ToList();

		var requestPlan = new Request(
			request.StartLine,
			requestLine.Method,
			requestLine.Target,
			requestLine.Version,
			url,
			hostHeader,
			ParseQueryParameters(url),
			requestHeaders,
			ParseCookies(GetHeader(request.Headers, "Cookie")),
			dynamicHeaders,
			requestBody,
			requestJsonBody,
			requestFormBody,
			BuildRequestSteps(requestLine, requestHeaders, dynamicHeaders, requestBody)
		);

		var responsePlan = new Response(
			response.StartLine,
			statusLine.Code,
			statusLine.ReasonPhrase,
			SortHeaders(response.Headers),
			responseBody,
			responseText,
			responseJson,
			BuildResponseSteps(statusLine, response.Headers, responseBody)
		);

		return new Session(
			raw.Id,
			metadata,
			requestPlan,
			responsePlan
		);
	}

	static Metadata ParseMetadata(byte[]? metadataBytes, int sessionId) {
		if (metadataBytes is null || metadataBytes.Length == 0)
			return new Metadata(new Dictionary<string, string>(), new Dictionary<string, string>());

		var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var timers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		XDocument doc;
		try {
			using var stream = new MemoryStream(metadataBytes);
			doc = XDocument.Load(stream, LoadOptions.None);
		}
		catch (Exception ex) {
			LogMalformedMetadata(sessionId, metadataBytes, ex);
			return new Metadata(flags, timers);
		}

		foreach (var flag in doc.Descendants("SessionFlag")) {
			var key = flag.Attribute("N")?.Value;
			var value = flag.Attribute("V")?.Value;
			if (!string.IsNullOrWhiteSpace(key) && value is not null)
				flags[key] = value;
		}

		var timersElement = doc.Descendants("SessionTimers").FirstOrDefault();
		if (timersElement is not null)
			foreach (var attr in timersElement.Attributes())
				timers[attr.Name.LocalName] = attr.Value;

		return new Metadata(flags, timers);
	}

	static void LogMalformedMetadata(int sessionId, byte[] metadataBytes, Exception ex) {
		var utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
		var utf8 = Encoding.UTF8.GetString(metadataBytes);
		var latin1 = Encoding.Latin1.GetString(metadataBytes);
		var base64 = Convert.ToBase64String(metadataBytes);

		var entry = $"[{utc}] Session {sessionId} malformed metadata. "
			+ $"Length={metadataBytes.Length}. Error={ex.GetType().Name}: {ex.Message}{Environment.NewLine}"
			+ $"UTF8:{Environment.NewLine}{utf8}{Environment.NewLine}" 
			+ $"Latin1:{Environment.NewLine}{latin1}{Environment.NewLine}"
			+ $"Base64:{Environment.NewLine}{base64}{Environment.NewLine}"
			+ $"{new string('-', 80)}{Environment.NewLine}";

		lock (MalformedMetadataLogLock) {
			File.AppendAllText(MalformedMetadataLogPath, entry, Encoding.UTF8);
		}
	}

	// splits the rawBytes of a request or response into headers and body-bytes
	static HttpMessageParts ParseHttpMessage(byte[]? rawBytes, bool isRequest) {
		if (rawBytes is null || rawBytes.Length == 0)
			return new HttpMessageParts(string.Empty, new Dictionary<string, string>(), Array.Empty<byte>());

		(byte[] headerBytes, byte[] bodyBytes) = FindHeaderBodySeparator(rawBytes);

		string headerText = Encoding.Latin1.GetString(headerBytes);
		string[] lines = headerText.Split(["\r\n", "\n"], StringSplitOptions.None);
		string startLine = lines.FirstOrDefault() ?? string.Empty;

		Dictionary<string, string> headers = ParseHeaders(lines.Skip(1));

		if (isRequest && !headers.ContainsKey("Host") && startLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase)) {
			string? target = startLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
			if (!string.IsNullOrWhiteSpace(target))
				headers["Host"] = target;
		}

		return new HttpMessageParts(startLine, headers, bodyBytes);
	}

	static (byte[] headerBytes, byte[] bodyBytes) FindHeaderBodySeparator(byte[] rawBytes) {
		var crlf = new byte[] { 13, 10, 13, 10 };
		var lf = new byte[] { 10, 10 };

		var idx = IndexOf(rawBytes, crlf);
		if (idx >= 0) {
			return (
				rawBytes[..idx],
				rawBytes[(idx + crlf.Length)..]
			);
		}

		idx = IndexOf(rawBytes, lf);
		if (idx >= 0) {
			return (
				rawBytes[..idx],
				rawBytes[(idx + lf.Length)..]
			);
		}

		return (rawBytes, Array.Empty<byte>());
	}

	static int IndexOf(byte[] data, byte[] marker) {
		if (marker.Length == 0 || data.Length < marker.Length)
			return -1;


		for (var i = 0; i <= data.Length - marker.Length; i++) {
			var matched = true;
			for (var j = 0; j < marker.Length; j++) {
				if (data[i + j] != marker[j]) {
					matched = false;
					break;
				}
			}

			if (matched)
				return i;
		}

		return -1;
	}

	static Dictionary<string, string> ParseHeaders(IEnumerable<string> headerLines) {
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string? currentHeader = null;

		foreach (var rawLine in headerLines) {
			if (string.IsNullOrEmpty(rawLine))
				continue;

			if ((rawLine.StartsWith(' ') || rawLine.StartsWith('\t')) && currentHeader is not null) {
				headers[currentHeader] = headers[currentHeader] + " " + rawLine.Trim();
				continue;
			}

			var idx = rawLine.IndexOf(':');
			if (idx <= 0)
				continue;

			var name = rawLine[..idx].Trim();
			var value = rawLine[(idx + 1)..].Trim();
			if (name.Length == 0)
				continue;

			currentHeader = name;
			headers[name] = value;
		}

		return headers;
	}

	static RequestLineParts ParseRequestLine(string startLine) {
		var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
		var method = parts.Length > 0 ? parts[0] : "GET";
		var target = parts.Length > 1 ? parts[1] : "/";
		var version = parts.Length > 2 ? parts[2] : "HTTP/1.1";
		return new RequestLineParts(method, target, version);
	}

	static StatusLineParts ParseStatusLine(string startLine) {
		var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

		var code = 0;
		if (parts.Length > 1)
			int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out code);

		var reason = parts.Length > 2 ? parts[2] : string.Empty;
		return new StatusLineParts(code, reason);
	}

	static string BuildUrl(string method, string target, string? host, Metadata? metadata) {
		if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
			return absolute.ToString();

		if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
			return "https://" + target;

		var scheme = metadata?.Flags.TryGetValue("x-overrideGateway", out var gateway) == true 
			&& gateway.Contains("https", StringComparison.OrdinalIgnoreCase)
			? "https"
			: "http";

		if (!string.IsNullOrWhiteSpace(host)) {
			var normalizedTarget = target.StartsWith('/') ? target : "/" + target;
			return $"{scheme}://{host}{normalizedTarget}";
		}

		return target;
	}

	static Dictionary<string, string> ParseQueryParameters(string url) {
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Query))
			return result;


		var query = uri.Query.TrimStart('?');
		foreach (var item in query.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = item.Split('=', 2);
			var name = Uri.UnescapeDataString(parts[0]);
			var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
			result[name] = value;
		}

		return result;
	}

	static Dictionary<string, string> ParseCookies(string? cookieHeader) {
		var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		if (string.IsNullOrWhiteSpace(cookieHeader))
			return cookies;

		foreach (var piece in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = piece.Split('=', 2);
			if (parts.Length == 0)
				continue;

			var key = parts[0].Trim();
			var value = parts.Length > 1 ? parts[1].Trim() : string.Empty;

			if (!string.IsNullOrWhiteSpace(key))
				cookies[key] = value;

		}

		return cookies;
	}

	static string? GetHeader(Dictionary<string, string> headers, string name) {
		return headers.TryGetValue(name, out var value) ? value : null;
	}


	static List<string> BuildRequestSteps(
		RequestLineParts requestLine,
		Dictionary<string, string> headers,
		List<string> dynamicHeaders,
		Body body) {
		var steps = new List<string> {
			$"Create {requestLine.Method} request using HTTP version {requestLine.Version}.",
			"Set URL from host + target path and include query parameters.",
			"Apply static headers captured from the original session.",
		};

		if (dynamicHeaders.Count > 0)
			steps.Add("Generate or refresh dynamic headers: " + string.Join(", ", dynamicHeaders) + ".");


		if (headers.ContainsKey("Authorization"))
			steps.Add("Replace Authorization header with a valid token/credential at runtime.");


		if (body.Length > 0) {
			steps.Add($"Build request body as {body.Format} with Content-Type '{body.ContentType}'.");
			if (body.SchemaHints.Count > 0)
				steps.Add("Populate body fields: " + string.Join(", ", body.SchemaHints) + ".");
		}

		return steps;
	}

	static List<string> BuildResponseSteps(
		StatusLineParts status,
		Dictionary<string, string> headers,
		Body body) {
		var steps = new List<string>
		{
			$"Expect HTTP status code {status.Code} ({status.ReasonPhrase}).",
			"Validate key response headers.",
		};

		if (headers.ContainsKey("Set-Cookie"))
			steps.Add("Capture Set-Cookie values for follow-up requests.");


		if (body.Length > 0) {
			steps.Add($"Validate response body format as {body.Format}.");
			if (body.SchemaHints.Count > 0)
				steps.Add("Verify response fields: " + string.Join(", ", body.SchemaHints) + ".");
		}

		return steps;
	}

	private static Dictionary<string, string> SortHeaders(Dictionary<string, string> headers) {
		return headers
			.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
	}
}
