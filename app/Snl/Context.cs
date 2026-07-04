using Automation;

namespace Snl;

internal class Context {

	// Set after instantiation, before any Steps methods are called.
	public IAuthHttpClient? Http { get; set; }

	// The index/landing page HTML retrieved by GetIndexPageAsync.
	public string? IndexPageHtml { get; set; }

	// Absolute src URLs of <script> tags found in IndexPageHtml, populated by GetIndexPageAsync.
	public List<string>? JavascriptScripts { get; set; }

}
