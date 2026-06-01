using System.Text.Json;

internal static class AzureB2cAuthenticationScanner {
	static readonly string[] B2cQueryKeys =
	[
		"client_id",
		"redirect_uri",
		"code_challenge",
		"code_verifier",
		"nonce",
		"state",
		"grant_type",
	];

	public static AzureB2cAuthenticationReport Scan(IReadOnlyList<Session> sessions) {
		var flows = new Dictionary<string, AzureB2cFlowAccumulator>(StringComparer.OrdinalIgnoreCase);

		foreach (var session in sessions) {
			if (!TryBuildCandidate(session, out var host, out var policy, out var indicators, out var isAuthorize, out var isToken, out var hasTokenPayload))
				continue;

			var key = BuildFlowKey(host, policy);
			if (!flows.TryGetValue(key, out var flow)) {
				flow = new AzureB2cFlowAccumulator(host, policy);
				flows[key] = flow;
			}

			flow.SessionIds.Add(session.SessionId);
			if (isAuthorize)
				flow.AuthorizeSessionIds.Add(session.SessionId);

			if (isToken)
				flow.TokenSessionIds.Add(session.SessionId);

			if (hasTokenPayload)
				flow.HasTokenPayload = true;

			foreach (var indicator in indicators)
				flow.Indicators.Add(indicator);
		}

		var reportFlows = flows.Values
			.Select(flow => new AzureB2cAuthenticationFlow(
				flow.Host,
				flow.Policy,
				flow.SessionIds.Order().ToList(),
				flow.AuthorizeSessionIds.Order().ToList(),
				flow.TokenSessionIds.Order().ToList(),
				flow.Indicators.Order(StringComparer.OrdinalIgnoreCase).ToList(),
				ComputeConfidence(flow)
			))
			.OrderBy(flow => flow.Host, StringComparer.OrdinalIgnoreCase)
			.ThenBy(flow => flow.Policy, StringComparer.OrdinalIgnoreCase)
			.ToList();

		return new AzureB2cAuthenticationReport(DateTimeOffset.UtcNow, reportFlows);
	}

	static bool TryBuildCandidate(
		Session session,
		out string host,
		out string? policy,
		out HashSet<string> indicators,
		out bool isAuthorize,
		out bool isToken,
		out bool hasTokenPayload
	) {
		host = GetNormalizedHost(session);
		policy = ExtractPolicy(session.Request.Url);
		indicators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		isAuthorize = IsAuthorizeRequest(session.Request.Url);
		isToken = IsTokenRequest(session.Request.Url);
		hasTokenPayload = HasTokenPayload(session.Response);

		if (host.EndsWith(".b2clogin.com", StringComparison.OrdinalIgnoreCase))
			indicators.Add("host:b2clogin");

		if (IsOpenIdConfiguration(session.Request.Url))
			indicators.Add("endpoint:openid-configuration");

		if (isAuthorize)
			indicators.Add("endpoint:authorize");

		if (isToken)
			indicators.Add("endpoint:token");

		if (HasB2cQueryOrFormKeys(session.Request))
			indicators.Add("params:oauth");

		if (hasTokenPayload)
			indicators.Add("response:tokens");

		if (string.IsNullOrWhiteSpace(host) && indicators.Count == 0)
			return false;

		if (indicators.Count == 0)
			return false;

		return true;
	}

	static int ComputeConfidence(AzureB2cFlowAccumulator flow) {
		var score = 0;
		if (flow.Host.EndsWith(".b2clogin.com", StringComparison.OrdinalIgnoreCase))
			score += 40;

		if (flow.AuthorizeSessionIds.Count > 0)
			score += 20;

		if (flow.TokenSessionIds.Count > 0)
			score += 20;

		if (flow.Indicators.Contains("params:oauth"))
			score += 10;

		if (flow.HasTokenPayload)
			score += 10;

		return Math.Min(score, 100);
	}

	static string BuildFlowKey(string host, string? policy) {
		return string.IsNullOrWhiteSpace(policy) ? host : $"{host}|{policy}";
	}

	static bool HasB2cQueryOrFormKeys(Request request) {
		if (request.QueryParameters.Keys.Any(IsB2cKey))
			return true;

		if (request.FormBody is { Count: > 0 } && request.FormBody.Any(entry => IsB2cKey(entry.Key)))
			return true;

		return false;
	}

	static bool IsB2cKey(string key) {
		return B2cQueryKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
	}

	static bool HasTokenPayload(Response response) {
		if (response.ResponseJson is not JsonElement json || json.ValueKind != JsonValueKind.Object)
			return false;

		return json.TryGetProperty("id_token", out _)
			|| json.TryGetProperty("access_token", out _)
			|| json.TryGetProperty("refresh_token", out _)
			|| json.TryGetProperty("token_type", out _)
			|| json.TryGetProperty("expires_in", out _);
	}

	static bool IsAuthorizeRequest(string url) {
		return ContainsPath(url, "/oauth2/v2.0/authorize");
	}

	static bool IsTokenRequest(string url) {
		return ContainsPath(url, "/oauth2/v2.0/token");
	}

	static bool IsOpenIdConfiguration(string url) {
		return ContainsPath(url, "/.well-known/openid-configuration");
	}

	static bool ContainsPath(string url, string marker) {
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			return url.Contains(marker, StringComparison.OrdinalIgnoreCase);

		return uri.AbsolutePath.Contains(marker, StringComparison.OrdinalIgnoreCase);
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

	static string? ExtractPolicy(string url) {
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			return null;

		var segments = uri.AbsolutePath
			.Split('/', StringSplitOptions.RemoveEmptyEntries);

		for (var i = 0; i < segments.Length; i++) {
			if (segments[i].StartsWith("b2c_", StringComparison.OrdinalIgnoreCase))
				return segments[i];
		}

		return null;
	}

	sealed class AzureB2cFlowAccumulator {
		public string Host { get; }
		public string? Policy { get; }
		public HashSet<int> SessionIds { get; } = [];
		public HashSet<int> AuthorizeSessionIds { get; } = [];
		public HashSet<int> TokenSessionIds { get; } = [];
		public HashSet<string> Indicators { get; } = new(StringComparer.OrdinalIgnoreCase);
		public bool HasTokenPayload { get; set; }

		public AzureB2cFlowAccumulator(string host, string? policy) {
			Host = host;
			Policy = policy;
		}
	}
}

internal sealed record AzureB2cAuthenticationReport(
	DateTimeOffset GeneratedUtc,
	List<AzureB2cAuthenticationFlow> Flows
);

internal sealed record AzureB2cAuthenticationFlow(
	string Host,
	string? Policy,
	List<int> SessionIds,
	List<int> AuthorizeSessionIds,
	List<int> TokenSessionIds,
	List<string> Indicators,
	int ConfidenceScore
);