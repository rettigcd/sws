using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class SazPlanBuilder
{
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
	private static readonly Regex RawFilePattern = new(@"^raw\/(?<id>\d+)_(?<kind>[csm])\.(?<ext>txt|xml)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
	private static readonly HashSet<string> DynamicHeaderNames =
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

	public static SazPlan Build(
		string sazPath,
		bool includeConnect = false,
		bool includeCss = false,
		bool includeMedia = false,
		bool includeMetadata = false,
		bool includeSourcemaps = false)
	{
		using var stream = File.OpenRead(sazPath);
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

		var map = new Dictionary<int, SessionRaw>();

		foreach (var entry in archive.Entries)
		{
			var match = RawFilePattern.Match(entry.FullName);
			if (!match.Success)
			{
				continue;
			}

			var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
			var kind = match.Groups["kind"].Value.ToLowerInvariant();

			if (!map.TryGetValue(id, out var sessionRaw))
			{
				sessionRaw = new SessionRaw(id);
				map[id] = sessionRaw;
			}

			using var entryStream = entry.Open();
			using var memory = new MemoryStream();
			entryStream.CopyTo(memory);
			var bytes = memory.ToArray();

			switch (kind)
			{
				case "c":
					sessionRaw.ClientRequestBytes = bytes;
					break;
				case "s":
					sessionRaw.ServerResponseBytes = bytes;
					break;
				case "m":
					sessionRaw.MetadataBytes = bytes;
					break;
			}
		}

		var sessions = map
			.OrderBy(kvp => kvp.Key)
			.Select(kvp => BuildSessionPlan(kvp.Value, includeMetadata))
			.Where(session =>
				(includeConnect || !IsConnectSession(session)) &&
				(includeCss || !IsCssSession(session)) &&
				(includeMedia || !IsMediaSession(session)) &&
				(includeSourcemaps || !IsSourcemapSession(session)))
			.ToList();

		var globalHeaders = BuildGlobalHeadersGroup(sessions);
		var sessionsWithoutGlobalHeaders = RemoveGlobalHeaders(sessions, globalHeaders.Headers);

		return new SazPlan(
			Path.GetFullPath(sazPath),
			DateTimeOffset.UtcNow,
			globalHeaders,
			sessionsWithoutGlobalHeaders
		);
	}

	public static string WriteSessionSourcesReport(string outputBasePath, int sessionIndex, IReadOnlyList<SessionPlan> sessions)
	{
		if (sessionIndex < 1 || sessionIndex > sessions.Count)
		{
			throw new ArgumentOutOfRangeException(nameof(sessionIndex), sessionIndex, $"Session index must be between 1 and {sessions.Count}.");
		}

		var outputPath = Path.ChangeExtension(outputBasePath, ".sources.json");
		var options = new JsonSerializerOptions
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.Never,
		};
		var report = SourceReportBuilder.BuildSessionSourcesReport(sessionIndex, sessions);
		File.WriteAllText(outputPath, JsonSerializer.Serialize(report, options), Encoding.UTF8);
		return outputPath;
	}

	public static string WriteAllSessionSourcesReport(string outputBasePath, IReadOnlyList<SessionPlan> sessions)
	{
		var outputPath = Path.ChangeExtension(outputBasePath, ".sources.json");
		var options = new JsonSerializerOptions
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.Never,
		};

		var missing = new List<string>();
		var missingIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		var report = new SessionSourcesBatchReport(
			Path.GetFullPath(outputBasePath),
			missing,
			sessions
				.Select((session, index) => SourceReportBuilder.BuildSessionSourcesReport(index + 1, sessions, missing, missingIndexes))
				.ToList()
		);

		File.WriteAllText(outputPath, JsonSerializer.Serialize(report, options), Encoding.UTF8);
		return outputPath;
	}

	private static GlobalHeadersGroupPlan BuildGlobalHeadersGroup(List<SessionPlan> sessions)
	{
		if (sessions.Count == 0)
		{
			return new GlobalHeadersGroupPlan("global-headers", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
		}

		var commonHeaders = sessions[0].Request.Headers
			.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);

		foreach (var session in sessions.Skip(1))
		{
			var toRemove = commonHeaders
				.Where(commonHeader =>
					!session.Request.Headers.TryGetValue(commonHeader.Key, out var value) ||
					!string.Equals(value, commonHeader.Value, StringComparison.Ordinal))
				.Select(commonHeader => commonHeader.Key)
				.ToList();

			foreach (var key in toRemove)
			{
				commonHeaders.Remove(key);
			}
		}

		return new GlobalHeadersGroupPlan("global-headers", SortHeaders(commonHeaders));
	}


	private static List<SessionPlan> RemoveGlobalHeaders(
		List<SessionPlan> sessions,
		Dictionary<string, string> globalHeaders)
	{
		if (globalHeaders.Count == 0)
		{
			return sessions;
		}

		return sessions
			.Select(session =>
			{
				var requestHeaders = session.Request.Headers
					.Where(header => !globalHeaders.ContainsKey(header.Key))
					.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);

				var request = session.Request with
				{
					Headers = SortHeaders(requestHeaders),
				};

				return session with { Request = request };
			})
			.ToList();
	}

	private static bool IsConnectSession(SessionPlan session)
	{
		return string.Equals(session.Request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsCssSession(SessionPlan session)
	{
		if (HasPathExtension(session.Request.Url, ".css"))
		{
			return true;
		}

		if (session.Response.Headers.TryGetValue("Content-Type", out var responseContentType) &&
			responseContentType.Contains("text/css", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return false;
	}

	private static bool IsMediaSession(SessionPlan session)
	{
		if (HasPathExtension(session.Request.Url, MediaPathExtensions))
		{
			return true;
		}

		if (session.Response.Headers.TryGetValue("Content-Type", out var responseContentType))
		{
			if (responseContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
				responseContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
				responseContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
				responseContentType.StartsWith("font/", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			if (responseContentType.Contains("application/font", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	private static bool IsSourcemapSession(SessionPlan session)
	{
		return HasPathExtension(session.Request.Url, ".map");
	}

	private static bool HasPathExtension(string urlOrPath, string extension)
	{
		if (string.IsNullOrWhiteSpace(urlOrPath))
		{
			return false;
		}

		if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri))
		{
			return uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
		}

		var path = urlOrPath.Split('?', 2)[0];
		return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
	}

	private static bool HasPathExtension(string urlOrPath, HashSet<string> extensions)
	{
		if (string.IsNullOrWhiteSpace(urlOrPath))
		{
			return false;
		}

		string path;
		if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri))
		{
			path = uri.AbsolutePath;
		}
		else
		{
			path = urlOrPath.Split('?', 2)[0];
		}

		var extension = Path.GetExtension(path);
		if (string.IsNullOrWhiteSpace(extension))
		{
			return false;
		}

		return extensions.Contains(extension.ToLowerInvariant());
	}

	private static SessionPlan BuildSessionPlan(SessionRaw raw, bool includeMetadata)
	{
		var metadata = includeMetadata ? ParseMetadata(raw.MetadataBytes, raw.Id) : null;
		var request = ParseHttpMessage(raw.ClientRequestBytes, isRequest: true);
		var response = ParseHttpMessage(raw.ServerResponseBytes, isRequest: false);

		var requestLine = ParseRequestLine(request.StartLine);
		var statusLine = ParseStatusLine(response.StartLine);
		var hostHeader = GetHeader(request.Headers, "Host");

		var url = BuildUrl(requestLine.Method, requestLine.Target, hostHeader, metadata);
		var requestBodyText = DecodeBodyText(request.Headers, request.BodyBytes);
		var requestBody = BuildBodyPlan(request.Headers, request.BodyBytes);
		var requestJsonBody = BuildRequestJsonBody(requestBody.Format, requestBodyText);
		var requestFormBody = BuildRequestFormBody(requestBody.Format, requestBodyText);
		var responseBodyText = DecodeBodyText(response.Headers, response.BodyBytes);
		var responseBody = BuildBodyPlan(response.Headers, response.BodyBytes);
		var responseText = BuildResponseText(response.Headers, responseBodyText);
		var responseJson = BuildResponseJson(responseBody.Format, responseBodyText);

		var requestHeaders = request.Headers
			.Where(h => !DynamicHeaderNames.Contains(h.Key.ToLowerInvariant()))
			.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);

		var dynamicHeaders = request.Headers
			.Where(h => DynamicHeaderNames.Contains(h.Key.ToLowerInvariant()))
			.Select(h => h.Key)
			.ToList();

		var requestPlan = new RequestPlan(
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

		var responsePlan = new ResponsePlan(
			response.StartLine,
			statusLine.Code,
			statusLine.ReasonPhrase,
			SortHeaders(response.Headers),
			responseBody,
			responseText,
			responseJson,
			BuildResponseSteps(statusLine, response.Headers, responseBody)
		);

		return new SessionPlan(
			raw.Id,
			metadata,
			requestPlan,
			responsePlan
		);
	}

	private static MetadataPlan ParseMetadata(byte[]? metadataBytes, int sessionId)
	{
		if (metadataBytes is null || metadataBytes.Length == 0)
		{
			return new MetadataPlan(new Dictionary<string, string>(), new Dictionary<string, string>());
		}

		var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var timers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		XDocument doc;
		try
		{
			using var stream = new MemoryStream(metadataBytes);
			doc = XDocument.Load(stream, LoadOptions.None);
		}
		catch (Exception ex)
		{
			LogMalformedMetadata(sessionId, metadataBytes, ex);
			return new MetadataPlan(flags, timers);
		}

		foreach (var flag in doc.Descendants("SessionFlag"))
		{
			var key = flag.Attribute("N")?.Value;
			var value = flag.Attribute("V")?.Value;
			if (!string.IsNullOrWhiteSpace(key) && value is not null)
			{
				flags[key] = value;
			}
		}

		var timersElement = doc.Descendants("SessionTimers").FirstOrDefault();
		if (timersElement is not null)
		{
			foreach (var attr in timersElement.Attributes())
			{
				timers[attr.Name.LocalName] = attr.Value;
			}
		}

		return new MetadataPlan(flags, timers);
	}

	private static void LogMalformedMetadata(int sessionId, byte[] metadataBytes, Exception ex)
	{
		var utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
		var utf8 = Encoding.UTF8.GetString(metadataBytes);
		var latin1 = Encoding.Latin1.GetString(metadataBytes);
		var base64 = Convert.ToBase64String(metadataBytes);

		var entry =
			$"[{utc}] Session {sessionId} malformed metadata. " +
			$"Length={metadataBytes.Length}. Error={ex.GetType().Name}: {ex.Message}{Environment.NewLine}" +
			$"UTF8:{Environment.NewLine}{utf8}{Environment.NewLine}" +
			$"Latin1:{Environment.NewLine}{latin1}{Environment.NewLine}" +
			$"Base64:{Environment.NewLine}{base64}{Environment.NewLine}" +
			$"{new string('-', 80)}{Environment.NewLine}";

		lock (MalformedMetadataLogLock)
		{
			File.AppendAllText(MalformedMetadataLogPath, entry, Encoding.UTF8);
		}
	}

	private static HttpMessageParts ParseHttpMessage(byte[]? rawBytes, bool isRequest)
	{
		if (rawBytes is null || rawBytes.Length == 0)
		{
			return new HttpMessageParts(string.Empty, new Dictionary<string, string>(), Array.Empty<byte>());
		}

		var split = FindHeaderBodySeparator(rawBytes);
		var headerBytes = split.headerBytes;
		var bodyBytes = split.bodyBytes;

		var headerText = Encoding.Latin1.GetString(headerBytes);
		var lines = headerText.Split(["\r\n", "\n"], StringSplitOptions.None);
		var startLine = lines.FirstOrDefault() ?? string.Empty;

		var headers = ParseHeaders(lines.Skip(1));

		if (isRequest && !headers.ContainsKey("Host") && startLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
		{
			var target = startLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault();
			if (!string.IsNullOrWhiteSpace(target))
			{
				headers["Host"] = target;
			}
		}

		return new HttpMessageParts(startLine, headers, bodyBytes);
	}

	private static (byte[] headerBytes, byte[] bodyBytes) FindHeaderBodySeparator(byte[] rawBytes)
	{
		var crlf = new byte[] { 13, 10, 13, 10 };
		var lf = new byte[] { 10, 10 };

		var idx = IndexOf(rawBytes, crlf);
		if (idx >= 0)
		{
			return (
				rawBytes[..idx],
				rawBytes[(idx + crlf.Length)..]
			);
		}

		idx = IndexOf(rawBytes, lf);
		if (idx >= 0)
		{
			return (
				rawBytes[..idx],
				rawBytes[(idx + lf.Length)..]
			);
		}

		return (rawBytes, Array.Empty<byte>());
	}

	private static int IndexOf(byte[] data, byte[] marker)
	{
		if (marker.Length == 0 || data.Length < marker.Length)
		{
			return -1;
		}

		for (var i = 0; i <= data.Length - marker.Length; i++)
		{
			var matched = true;
			for (var j = 0; j < marker.Length; j++)
			{
				if (data[i + j] != marker[j])
				{
					matched = false;
					break;
				}
			}

			if (matched)
			{
				return i;
			}
		}

		return -1;
	}

	private static Dictionary<string, string> ParseHeaders(IEnumerable<string> headerLines)
	{
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string? currentHeader = null;

		foreach (var rawLine in headerLines)
		{
			if (string.IsNullOrEmpty(rawLine))
			{
				continue;
			}

			if ((rawLine.StartsWith(' ') || rawLine.StartsWith('\t')) && currentHeader is not null)
			{
				headers[currentHeader] = headers[currentHeader] + " " + rawLine.Trim();
				continue;
			}

			var idx = rawLine.IndexOf(':');
			if (idx <= 0)
			{
				continue;
			}

			var name = rawLine[..idx].Trim();
			var value = rawLine[(idx + 1)..].Trim();
			if (name.Length == 0)
			{
				continue;
			}

			currentHeader = name;
			headers[name] = value;
		}

		return headers;
	}

	private static RequestLineParts ParseRequestLine(string startLine)
	{
		var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
		var method = parts.Length > 0 ? parts[0] : "GET";
		var target = parts.Length > 1 ? parts[1] : "/";
		var version = parts.Length > 2 ? parts[2] : "HTTP/1.1";
		return new RequestLineParts(method, target, version);
	}

	private static StatusLineParts ParseStatusLine(string startLine)
	{
		var parts = startLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

		var code = 0;
		if (parts.Length > 1)
		{
			int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
		}

		var reason = parts.Length > 2 ? parts[2] : string.Empty;
		return new StatusLineParts(code, reason);
	}

	private static string BuildUrl(string method, string target, string? host, MetadataPlan? metadata)
	{
		if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
		{
			return absolute.ToString();
		}

		if (string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase))
		{
			return "https://" + target;
		}

		var scheme = metadata?.Flags.TryGetValue("x-overrideGateway", out var gateway) == true &&
					 gateway.Contains("https", StringComparison.OrdinalIgnoreCase)
			? "https"
			: "http";

		if (!string.IsNullOrWhiteSpace(host))
		{
			var normalizedTarget = target.StartsWith('/') ? target : "/" + target;
			return $"{scheme}://{host}{normalizedTarget}";
		}

		return target;
	}

	private static Dictionary<string, string> ParseQueryParameters(string url)
	{
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Query))
		{
			return result;
		}

		var query = uri.Query.TrimStart('?');
		foreach (var item in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = item.Split('=', 2);
			var name = Uri.UnescapeDataString(parts[0]);
			var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
			result[name] = value;
		}

		return result;
	}

	private static Dictionary<string, string> ParseCookies(string? cookieHeader)
	{
		var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		if (string.IsNullOrWhiteSpace(cookieHeader))
		{
			return cookies;
		}

		foreach (var piece in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = piece.Split('=', 2);
			if (parts.Length == 0)
			{
				continue;
			}

			var key = parts[0].Trim();
			var value = parts.Length > 1 ? parts[1].Trim() : string.Empty;

			if (!string.IsNullOrWhiteSpace(key))
			{
				cookies[key] = value;
			}
		}

		return cookies;
	}

	private static string? GetHeader(Dictionary<string, string> headers, string name)
	{
		return headers.TryGetValue(name, out var value) ? value : null;
	}

	private static BodyPlan BuildBodyPlan(Dictionary<string, string> headers, byte[] bodyBytes)
	{
		var contentType = GetHeader(headers, "Content-Type");
		var bodyText = DecodeBodyText(headers, bodyBytes);

		var format = GuessBodyFormat(contentType, bodyText);

		var schemaHints = new List<string>();
		if (format == "json" && !string.IsNullOrWhiteSpace(bodyText))
		{
			schemaHints.AddRange(ExtractJsonTopLevelKeys(bodyText));
		}
		else if (format == "form-url-encoded")
		{
			schemaHints.AddRange(ParseFormKeys(bodyText));
		}

		return new BodyPlan(
			bodyBytes.Length,
			contentType,
			format,
			schemaHints.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
		);
	}

	private static string DecodeBodyText(Dictionary<string, string> headers, byte[] bodyBytes)
	{
		if (bodyBytes.Length == 0)
		{
			return string.Empty;
		}

		var encoding = DetectEncoding(headers) ?? Encoding.UTF8;
		return SafeDecode(bodyBytes, encoding);
	}

	private static string? BuildResponseText(Dictionary<string, string> headers, string bodyText)
	{
		if (string.IsNullOrEmpty(bodyText))
		{
			return null;
		}

		var contentType = GetHeader(headers, "Content-Type") ?? string.Empty;
		if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
			contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
			contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
			contentType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase))
		{
			return bodyText;
		}

		return null;
	}

	private static JsonElement? BuildResponseJson(string format, string bodyText)
	{
		if (format != "json" || string.IsNullOrWhiteSpace(bodyText))
		{
			return null;
		}

		try
		{
			using var doc = JsonDocument.Parse(bodyText);
			return doc.RootElement.Clone();
		}
		catch
		{
			return null;
		}
	}

	private static JsonElement? BuildRequestJsonBody(string format, string bodyText)
	{
		if (format != "json" || string.IsNullOrWhiteSpace(bodyText))
		{
			return null;
		}

		try
		{
			using var doc = JsonDocument.Parse(bodyText);
			return doc.RootElement.Clone();
		}
		catch
		{
			return null;
		}
	}

	private static List<FormBodyEntry>? BuildRequestFormBody(string format, string bodyText)
	{
		if (format != "form-url-encoded" || string.IsNullOrWhiteSpace(bodyText))
		{
			return null;
		}

		var pairs = ParseFormPairs(bodyText);
		return pairs.Count > 0 ? pairs : null;
	}

	private static Encoding? DetectEncoding(Dictionary<string, string> headers)
	{
		var contentType = GetHeader(headers, "Content-Type");
		if (string.IsNullOrWhiteSpace(contentType))
		{
			return null;
		}

		var charsetIndex = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
		if (charsetIndex < 0)
		{
			return null;
		}

		var charset = contentType[(charsetIndex + "charset=".Length)..].Trim().Trim(';').Trim('"');
		if (charset.Length == 0)
		{
			return null;
		}

		try
		{
			return Encoding.GetEncoding(charset);
		}
		catch
		{
			return null;
		}
	}

	private static string SafeDecode(byte[] bytes, Encoding preferred)
	{
		try
		{
			return preferred.GetString(bytes);
		}
		catch
		{
			return Encoding.Latin1.GetString(bytes);
		}
	}

	private static string GuessBodyFormat(string? contentType, string bodyText)
	{
		var ct = contentType?.ToLowerInvariant() ?? string.Empty;

		if (ct.Contains("application/json") || bodyText.TrimStart().StartsWith('{') || bodyText.TrimStart().StartsWith('['))
		{
			return "json";
		}

		if (ct.Contains("application/x-www-form-urlencoded"))
		{
			return "form-url-encoded";
		}

		if (ct.Contains("xml") || bodyText.TrimStart().StartsWith('<'))
		{
			return "xml";
		}

		if (ct.Contains("multipart/form-data"))
		{
			return "multipart";
		}

		if (string.IsNullOrWhiteSpace(bodyText))
		{
			return "none";
		}

		return "text-or-binary";
	}

	private static List<string> ExtractJsonTopLevelKeys(string bodyText)
	{
		try
		{
			using var doc = JsonDocument.Parse(bodyText);
			if (doc.RootElement.ValueKind != JsonValueKind.Object)
			{
				return new List<string>();
			}

			return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
		}
		catch
		{
			return new List<string>();
		}
	}

	private static List<string> ParseFormKeys(string bodyText)
	{
		return ParseFormPairs(bodyText)
			.Select(pair => pair.Key)
			.ToList();
	}

	private static List<FormBodyEntry> ParseFormPairs(string bodyText)
	{
		var pairs = new List<FormBodyEntry>();

		foreach (var kvp in bodyText.Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = kvp.Split('=', 2);
			if (parts.Length == 0)
			{
				continue;
			}

			var key = DecodeFormComponent(parts[0]);
			if (string.IsNullOrWhiteSpace(key))
			{
				continue;
			}

			var value = parts.Length > 1 ? DecodeFormComponent(parts[1]) : string.Empty;
			pairs.Add(new FormBodyEntry(key, value));
		}

		return pairs;
	}

	private static string DecodeFormComponent(string value)
	{
		return Uri.UnescapeDataString(value.Replace('+', ' '));
	}

	private static List<string> BuildRequestSteps(
		RequestLineParts requestLine,
		Dictionary<string, string> headers,
		List<string> dynamicHeaders,
		BodyPlan body)
	{
		var steps = new List<string>
		{
			$"Create {requestLine.Method} request using HTTP version {requestLine.Version}.",
			"Set URL from host + target path and include query parameters.",
			"Apply static headers captured from the original session.",
		};

		if (dynamicHeaders.Count > 0)
		{
			steps.Add("Generate or refresh dynamic headers: " + string.Join(", ", dynamicHeaders) + ".");
		}

		if (headers.ContainsKey("Authorization"))
		{
			steps.Add("Replace Authorization header with a valid token/credential at runtime.");
		}

		if (body.Length > 0)
		{
			steps.Add($"Build request body as {body.Format} with Content-Type '{body.ContentType}'.");
			if (body.SchemaHints.Count > 0)
			{
				steps.Add("Populate body fields: " + string.Join(", ", body.SchemaHints) + ".");
			}
		}

		return steps;
	}

	private static List<string> BuildResponseSteps(
		StatusLineParts status,
		Dictionary<string, string> headers,
		BodyPlan body)
	{
		var steps = new List<string>
		{
			$"Expect HTTP status code {status.Code} ({status.ReasonPhrase}).",
			"Validate key response headers.",
		};

		if (headers.ContainsKey("Set-Cookie"))
		{
			steps.Add("Capture Set-Cookie values for follow-up requests.");
		}

		if (body.Length > 0)
		{
			steps.Add($"Validate response body format as {body.Format}.");
			if (body.SchemaHints.Count > 0)
			{
				steps.Add("Verify response fields: " + string.Join(", ", body.SchemaHints) + ".");
			}
		}

		return steps;
	}

	private static Dictionary<string, string> SortHeaders(Dictionary<string, string> headers)
	{
		return headers
			.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
	}
}
