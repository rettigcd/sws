namespace AzureB2c;

internal static class B2cCookieNames {
	static readonly string[] Prefixes = [
		"x-ms-cpim-",
	];

	public static bool IsB2cCookie(string cookieName) {
		foreach (string prefix in Prefixes)
			if (cookieName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return true;

		return false;
	}
}
