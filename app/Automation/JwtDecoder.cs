using System.Text;
using System.Text.Json;

namespace Automation;

/// <summary>
/// Decodes JWT claims for display purposes only - no signature verification. Tolerant of
/// opaque (non-JWT) access tokens, which are common and expected.
/// </summary>
internal static class JwtDecoder {

	public static List<Claim> Decode(string? token) {
		if (string.IsNullOrWhiteSpace(token))
			return [];

		var parts = token.Split('.');
		if (parts.Length < 2)
			return [];

		try {
			string payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
			using var document = JsonDocument.Parse(payloadJson);

			var claims = new List<Claim>();
			foreach (var property in document.RootElement.EnumerateObject()) {
				switch (property.Value.ValueKind) {
					case JsonValueKind.Array:
						foreach (var item in property.Value.EnumerateArray())
							claims.Add(new Claim(property.Name, item.ToString()));
						break;
					case JsonValueKind.Object:
						claims.Add(new Claim(property.Name, property.Value.GetRawText()));
						break;
					default:
						claims.Add(new Claim(property.Name, property.Value.ToString()));
						break;
				}
			}

			return claims;
		}
		catch (Exception ex) when (ex is FormatException or JsonException) {
			return [];
		}
	}

	static byte[] Base64UrlDecode(string value) {
		string padded = value.Replace('-', '+').Replace('_', '/');
		padded += new string('=', (4 - padded.Length % 4) % 4);
		return Convert.FromBase64String(padded);
	}
}
