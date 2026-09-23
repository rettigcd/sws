
using System.Text.RegularExpressions;

public static class InterestingFinder {

	static readonly Regex UrlTokenPattern = new("[A-Za-z0-9]+", RegexOptions.Compiled);

	static readonly HashSet<string> CommonWords = [
		"customer",
		"order",
		"product",
		"b2c_1a_signup_signin_passwordreset",
		"openid-configuration",
		".well-known"
	];

	static public bool IsInteresting(string token) {
		if (!UrlTokenPattern.IsMatch(token) 
			|| LooksLikePascalCaseIdentifier(token)
			|| IsValidHostName(token)
		)
			return false;

		if(IsKnownWord(token)) return false;
		return IsBase10Number( token )
			|| IsBase16Number( token )
			|| IsBase64UrlEncoded(token)
			|| LooksRandom(token);
	}

	static bool IsValidHostName(string host)
	{
		if (string.IsNullOrWhiteSpace(host))
			return false;

		if (host.Length > 253)
			return false;

		var labels = host.Split('.');

		// Require a domain + TLD
		if (labels.Length < 2)
			return false;

		foreach (string label in labels)
		{
			if (label.Length is < 1 or > 63)
				return false;

			if (!char.IsLetterOrDigit(label[0]))
				return false;

			if (!char.IsLetterOrDigit(label[^1]))
				return false;

			foreach (var ch in label)
			{
				if (!(char.IsLetterOrDigit(ch) || ch == '-'))
					return false;
			}
		}

		string tld = labels[^1];

		if (tld.Length < 2)
			return false;

		if (!tld.All(char.IsLetter))
			return false;

		return true;
	}

	static bool IsKnownWord(string token){
		return CommonWords.Contains(token);
	}

	static bool IsBase10Number(string token) {
		if (token.Length == 0)
			return false;

		foreach (var ch in token)
			if (!char.IsDigit(ch))
				return false;

		return true;
	}

	static bool IsBase16Number(string token) {
		if (token.Length == 0)
			return false;

		int start = 0;
		if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
			start = 2;

		if (start >= token.Length)
			return false;

		for (int i = start; i < token.Length; i++)
			if (!Uri.IsHexDigit(token[i]))
				return false;

		for (int i = start; i < token.Length; i++)
			if (!char.IsDigit(token[i]))
				return true;

		return false;
	}

	static bool LooksRandom(string token)
	{
		string letters = new(token.Where(char.IsLetter).ToArray());

		if (letters.Length < 8) return false;

		int vowels = letters.Count( c => "aeiouAEIOU".Contains(c) );

		double vowelRatio = (double)vowels / letters.Length;

		return vowelRatio < 0.30;
	}

	static bool LooksLikePascalCaseIdentifier(string token)
	{
		if (string.IsNullOrWhiteSpace(token))
			return false;

		if (!char.IsUpper(token[0]))
			return false;

		int wordCount = 0;
		int index = 0;

		while (index < token.Length)
		{
			if (!char.IsUpper(token[index]))
				return false;

			wordCount++;
			index++;

			// Allow acronym-style runs: UI, ID, XML, HTTP
			while (index < token.Length && char.IsUpper(token[index]))
				index++;

			// Allow normal word body: User, Version, Info
			while (index < token.Length && char.IsLower(token[index]))
				index++;

			// Allow optional digits at the end of each segment: Version2, IPv6
			while (index < token.Length && char.IsDigit(token[index]))
				index++;
		}

		return 0 < wordCount;
	}

	static bool IsBase64UrlEncoded(string token) {
		if (string.IsNullOrWhiteSpace(token))
			return false;

		// Base64URL values use A-Z a-z 0-9 - _ with optional '=' padding.
		foreach (var ch in token)
			if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '='))
				return false;

		if (token.Contains('=') && !token.EndsWith("=", StringComparison.Ordinal))
			return false;

		// Avoid overmatching very short words.
		if (token.Length < 16)
			return false;

		return true;
	}

}
