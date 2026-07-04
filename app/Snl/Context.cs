using Automation;

namespace Snl;

internal class Context {

	// Set after instantiation, before any Steps methods are called.
	public IAuthHttpClient? Http { get; set; }

	// Event-series identifier, e.g. "UZJLSRJUNZC". Set after instantiation, before GetIndexPageAsync is called.
	public string? SeriesId { get; set; }

	// Absolute src URLs of <script> tags found on the index page, populated by GetIndexPageAsync.
	// Populated via AddRange, not by replacing the list itself.
	public List<string> JavascriptScripts { get; } = [];

	// URL of the most recently requested page, used to supply the Referrer header on later requests.
	public string? CurrentPage { get; set; }

	// Value of the "Q-BW-SESSION-ID" cookie set by GetIndexPageAsync.
	public string? BwSessionId { get; set; }

}
