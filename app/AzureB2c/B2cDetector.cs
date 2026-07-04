namespace AzureB2c;

/// <summary>
/// Detects Azure AD B2C-specific signals in a URL: b2clogin.com/login.microsoftonline.com hosts,
/// tenant and policy names (path segment or "p=" query parameter).
/// </summary>
internal static class B2cDetector {

	public static bool IsB2cHost(string url) {
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			return false;

		return uri.Host.Contains("b2clogin.com", StringComparison.OrdinalIgnoreCase)
			|| uri.Host.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase);
	}

	public static (string? Tenant, string? Policy, string? AuthorityBaseUrl) Extract(Request request) {
		if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
			return (null, null, null);

		string? tenant = null;
		string? policy = null;

		foreach (string segment in uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
			if (tenant is null && segment.Contains(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase))
				tenant = segment;
			else if (policy is null && IsPolicyName(segment))
				policy = segment;
		}

		if (policy is null && request.QueryParameters.TryGetValue("p", out string? policyParam) && !string.IsNullOrWhiteSpace(policyParam))
			policy = policyParam;

		if (tenant is null && uri.Host.EndsWith(".b2clogin.com", StringComparison.OrdinalIgnoreCase))
			tenant = uri.Host[..uri.Host.IndexOf('.')];

		string authorityBaseUrl = $"{uri.Scheme}://{uri.Host}";
		return (tenant, policy, authorityBaseUrl);
	}

	static bool IsPolicyName(string segment) {
		return segment.StartsWith("b2c_1", StringComparison.OrdinalIgnoreCase);
	}
}
