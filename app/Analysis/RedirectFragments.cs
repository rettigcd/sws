namespace Analysis;

using Saz;

/// <summary>Copies a URL fragment seen in a redirect Location header onto the later request for that URL (fragments are never sent on the wire).</summary>
internal static class RedirectFragments {

	public static List<Exchange> Attach(List<Exchange> exchanges) {
		var locationFragmentsByUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var enriched = new List<Exchange>(exchanges.Count);

		foreach (var exchange in exchanges) {
			var request = exchange.Request;
			string normalizedUrl = NormalizeUrlWithoutFragment(request.Url);

			if (string.IsNullOrWhiteSpace(request.Fragment)
				&& !string.IsNullOrWhiteSpace(normalizedUrl)
				&& locationFragmentsByUrl.TryGetValue(normalizedUrl, out string? fragmentFromRedirect)) {
				request = request with { Fragment = fragmentFromRedirect };
			}

			enriched.Add(exchange with { Request = request });

			if (!exchange.Response.Headers.TryGetValue("Location", out string? location)
				|| string.IsNullOrWhiteSpace(location)
				|| !Uri.TryCreate(location, UriKind.Absolute, out var locationUri)) {
				continue;
			}

			string fragment = locationUri.Fragment.TrimStart('#');
			if (string.IsNullOrWhiteSpace(fragment))
				continue;

			string callbackUrl = NormalizeUrlWithoutFragment(locationUri.ToString());
			if (!string.IsNullOrWhiteSpace(callbackUrl))
				locationFragmentsByUrl[callbackUrl] = fragment;
		}

		return enriched;
	}

	static string NormalizeUrlWithoutFragment(string url) {
		if (string.IsNullOrWhiteSpace(url))
			return string.Empty;

		if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUrl)) {
			return absoluteUrl.GetLeftPart(UriPartial.Path) + absoluteUrl.Query;
		}

		int hashIndex = url.IndexOf('#', StringComparison.Ordinal);
		return hashIndex >= 0 ? url[..hashIndex] : url;
	}

}
