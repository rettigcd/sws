using Automation;

namespace Snl;

internal class Context {

	// Set after instantiation, before any Steps methods are called.
	public required IAuthHttpClient Http { get; set; }

	// Event-series identifier, e.g. "UZJLSRJUNZC". Set after instantiation, before GetIndexPageAsync is called.
	public string? SeriesId { get; set; }

	// Metadata for each event in the series, populated by GetSeriesEventsAsync.
	// Populated via AddRange, not by replacing the list itself.
	public List<EventMetadata> Events { get; } = [];

	// Set explicitly in GetXXXTicketsAsync after GetSeriesEventsAsync.
	public EventMetadata? SelectedEvent { get; set; }

	// Absolute src URLs of <script> tags found on the index page, populated by GetIndexPageAsync.
	// Populated via AddRange, not by replacing the list itself.
	public List<string> JavascriptScripts { get; } = [];

	// URL of the most recently requested page, used to supply the Referrer header on later requests.
	public string? CurrentPage { get; set; }

	// Value of the "Q-BW-SESSION-ID" cookie set by GetIndexPageAsync.
	public string? BwSessionId { get; set; }

	// Value of the "Q-BW-USER-ID" cookie set by GetIndexPageAsync.
	public string? BwUserId { get; set; }

	// Attendee details for CreateBookingAsync. Set after instantiation, before CreateBookingAsync is called.
	public UserInfo? User { get; set; }

	// Attendee group size for CreateBookingAsync. Defaults to 1.
	public int GroupSize { get; set; } = 1;

	// Booking confirmation reference number, e.g. "TI9KMM13KRG", populated by CreateBookingAsync.
	public string? BookingReferenceNumber { get; set; }

}
