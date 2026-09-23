#:property PublishAot=false
// Signs up for tickets through the Qudini booking widget, for either the SNL or the ice cream series, using only
// the five steps that are needed (steps 1, 4, 7, 9 and 13 in docs/SNL_TICKET_FLOW_SPEC.md, section 5).
// It sends dummy attendee data.
//
// Run:      dotnet run scripts/QudiniTickets.cs -- --site snl|icecream [--analytics] [--submit] [--list] [--title TEXT]
//                                                   [--event IDENTIFIER] [--group-size N] [--series ID]
// Default:  a dry run. Steps 1, 4, 7 and 9 are sent (they only read data and register a page-view session),
//           then the booking request is printed but NOT sent. Add --submit to send it.
// --list:   print every event in the series (identifier, title, start, seats) after step 7 and stop.
// Choosing: --event picks by identifier; --title picks the first upcoming event whose title contains TEXT (any case);
//           with neither, the first event that has not passed is used.
// Note:     --submit books with the dummy data and takes a real seat or ticket. Use it only when that is what you want.

// ==== TODO ====
// Create a User Preferences (seriesId, first, last, email, phone, show(event selector), group size, retry-strategy)
// Log ALL HTTP Exchanges
// Add the retry around the 1st step.
// Add retry around other steps. - What retry?
// Add Count-down timer.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var JsonOptions = new JsonSerializerOptions {
	PropertyNameCaseInsensitive = true,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

UserConfig[] configs = [
	new UserConfig {
		// SeriesId = "UZJLSRJUNZC",	// ice cream
		SeriesId = "B9KIOO7ZIQF",	// snl
		Show = "pie",				// event selector
		FirstName = "Test",
		LastName = "User",
		Email = "bob@thebuilder.com",
		Phone = "513-555-1212",
		GroupSize = 2
	}
];

var context = new Context();
var runConfig = new RunConfig();
Func<QudiniEvent, bool> available = e => !e.HasPassed && e.SlotsAvailable > 0;
Func<QudiniEvent, bool> selector = available;

const string Usage = "Usage: dotnet run scripts/QudiniTickets.cs -- --config INDEX|--site snl|icecream [--analytics] [--submit] [--list] [--title TEXT] [--event IDENTIFIER] [--group-size N] [--series ID]";
for (int i = 0; i < args.Length; i++) {
	switch (args[i]) {
		
		// series (ID) selection - selects: Ice Cream or SNL.
		case "--config" when i + 1 < args.Length: {
			if (!int.TryParse(args[++i], out int configIndex) || configIndex < 0 || configIndex >= configs.Length) {
				Console.Error.WriteLine($"Unknown config index. Use a value from 0 to {configs.Length - 1}.");
				Console.Error.WriteLine(Usage);
				return 2;
			}
			context.InitializeFrom(configs[configIndex]);
			selector = e => e.Title.Contains(context.Show, StringComparison.OrdinalIgnoreCase) && !e.HasPassed;
			break;
		}
		case "--site" when i + 1 < args.Length:{ 
			string siteName = args[++i].ToLowerInvariant();
			context.SeriesId = siteName switch { "snl" => "B9KIOO7ZIQF", "icecream" => "UZJLSRJUNZC", _ => "" };
			break;
		}
		case "--series" when i + 1 < args.Length: context.SeriesId = args[++i]; break;

		// event selection
		case "--event" when i + 1 < args.Length:{
			string eventIdentifier = args[++i];
			selector = e => e.Identifier == eventIdentifier;
			break;
		}
		case "--title" when i + 1 < args.Length:{
			string titleText = args[++i];
			selector = e => e.Title.Contains(titleText, StringComparison.OrdinalIgnoreCase) && !e.HasPassed;
			break;
		}

		// config options
		case "--analytics": runConfig.Analytics = true; break;
		case "--submit": runConfig.Submit = true; break;
		case "--list": runConfig.ListOnly = true; break;
		// user form info
		case "--group-size" when i + 1 < args.Length: context.GroupSize = int.Parse(args[++i]); break;

		default:
			Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
			Console.Error.WriteLine(Usage);
			return 2;
	}
}

if (context.SeriesId == "") {
	Console.Error.WriteLine("Missing or unknown --site (use snl or icecream).");
	Console.Error.WriteLine("OR --series [seriesId].");
	Console.Error.WriteLine(Usage);
	return 2;
}

using var handler = new HttpClientHandler {
	CookieContainer = context.CookieJar,
	UseCookies = true,
	AutomaticDecompression = DecompressionMethods.All,
};
using var http = new HttpClient(handler);
context.Http = http;

try {
	await Step1_GetBookingPage(context);

	if (runConfig.Analytics)
		await Step3_RegisterWidgetSession(context);

	await Step4_GetSeriesSettings(context);

	await Step7_GetEventsList(context);

	// ---- Exit Ramp ----
	if (runConfig.ListOnly) {
		foreach (var e in context.Events)
			Console.WriteLine($"   {e.Identifier,-12} {e.StartIso,-22} {e.SlotsAvailable,4} seats  max group {e.MaxGroupSize,2}  {e.Title}");
		return 0;
	}

	// Select event.
	context.SelectedEvent 
		= context.Events.Where(selector).FirstOrDefault()	// the one we want
		?? context.Events.Where(available).FirstOrDefault()	// fallback if desired one is unavailable
		?? throw new InvalidOperationException("No matching event found. Use --list to see the events.");
	Console.WriteLine($"   event {context.SelectedEvent.Identifier} (id {context.SelectedEvent.Id}) \"{context.SelectedEvent.Title}\" on {context.SelectedEvent.StartIso}, {context.SelectedEvent.SlotsAvailable} seats, max group {context.SelectedEvent.MaxGroupSize}");

	if (runConfig.Analytics)
		await Step8_PostFilterAnalytics(context);

	await Step9_StartEventBookingSession(context);

	if (runConfig.Analytics)
		await Step10_PostEventAnalytics(context);

	if (runConfig.Analytics)
		await Step12_PostBookingFormAnalytics(context);

	// ---- Exit Ramp ----
	if (!runConfig.Submit) {
		Console.WriteLine("13. booking request (dry run, NOT sent; pass --submit to send):");
		Console.WriteLine($"   POST {context.BaseUrl}/booking-widget/series/{context.SeriesId}/event/book");
		Console.WriteLine($"   {context.GetBookingJson()}");
		return 0;
	}

	await Step13_SubmitBooking(context);

	if (runConfig.Analytics)
		await Step14_PostBookingCompletionAnalytics(context);

	return 0;
}
catch (Exception ex) {
	Console.Error.WriteLine($"FAILED: {ex.Message}");
	return 1;
}

// ================================
// ======== Required Steps ========
// ================================

async Task Step1_GetBookingPage(Context context) {
	// ---- Step 1: open the booking page. ----
	// The response sets the cookies: (UserId,SessionId) that identify us (kept by the cookie jar).
	// This is the step that hangs and gives gateway failures.
	var index = new HttpRequestMessage(HttpMethod.Get, context.IndexUrl);
	AddBrowserHeaders(index);
	index.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
	index.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
	index.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
	index.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
	index.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
	index.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
	await SendAsync("1. open booking page", index);
	Console.WriteLine($"   user id {context.UserId}, session id {context.SessionId}");
}

async Task Step4_GetSeriesSettings(Context context) {
	// ---- Step 4: series settings. ----
	// Gets the series settings.
	//   max group size - helps us limit the group size
	//   attribution answer - The first attribution answer ("No answer") goes into the booking.
	context.SeriesSettings = JsonSerializer.Deserialize<SeriesSettings>(await SendAsync("4. get series settings", JsonGet($"{context.BaseUrl}/booking-widget/event/series/{context.SeriesId}")), JsonOptions)
		?? throw new InvalidOperationException("Series settings response was empty.");
	Console.WriteLine($"   attribution \"{context.SeriesSettings.DefaultAttributionQuestion}\", phone field {context.SeriesSettings.PhoneNumberState}");
}

async Task Step7_GetEventsList(Context context) {
	// ---- Step 7: the events list. ----
	// - numeric id goes into the booking
	// - short identifier goes into step 9.
	// - Slots available limit group size
	string eventsJson = await SendAsync("7. get events", JsonGet($"{context.BaseUrl}/booking-widget/event/events/{context.SeriesId}"));
	List<QudiniEvent> events = JsonSerializer.Deserialize<List<QudiniEvent>>(
		eventsJson,
		JsonOptions)
		?? throw new InvalidOperationException("Events response was empty.");
	context.Events = events
		.OrderBy(e => DateTimeOffset.Parse(e.StartIso))
		.ToList();
}

async Task Step9_StartEventBookingSession(Context context) {
	// ---- Step 9: create the event booking session (tells Qudini which event this visitor is looking at). ----
	EventBookingSessionRequest sessionRequest = new EventBookingSessionRequest {
		SessionId = context.SessionId,
		UserId = context.UserId,
		BrowserVersion = $"{context.ChromeVersion}.0.0.0",
		Referrer = context.IndexUrl,
	};
	await SendOptionalAsync("9. create event booking session",
		JsonPost($"{context.BaseUrl}/event-series/{context.SeriesId}/events/{context.SelectedEvent.Identifier}/session",
		JsonSerializer.Serialize(sessionRequest, JsonOptions))
	);
}

async Task Step13_SubmitBooking(Context context) {
	// ---- Step 13: submit the booking. ----
	var bookingResponse = JsonSerializer.Deserialize<BookingResponse>(await SendAsync("13. submit booking", JsonPost($"{context.BaseUrl}/booking-widget/series/{context.SeriesId}/event/book", context.GetBookingJson())), JsonOptions);
	context.BookingReference = bookingResponse?.ReferenceNumber;
	Console.WriteLine($"   booked. Reference number: {context.BookingReference ?? "(none in response)"}");
}

// =======================================
// ========  Analytical Steps  ===========
// =======================================

async Task Step3_RegisterWidgetSession(Context context) {
	// ---- Step 3: register the widget session. ----
	await SendOptionalAsync("3. register widget session", JsonPost(
		$"{context.BaseUrl}/event-series/{context.SeriesId}/session",
		JsonSerializer.Serialize(new WidgetSessionRegistrationRequest {
			UserId = context.UserId,
			Sessions = [new EventBookingSessionRequest {
				SessionId = context.SessionId,
				BrowserVersion = $"{context.ChromeVersion}.0.0.0",
				Referrer = context.IndexUrl,
			}],
		}, JsonOptions)));
}

async Task Step8_PostFilterAnalytics(Context context) {
	// ---- Step 8: report the selected date, topics, and store filters. ----
	await PostAnalyticsAsync("8. post filter analytics",
		ClickAnalyticsEvent.Null("Select Date", "Event Booking Date"),
		ClickAnalyticsEvent.Null("Select Topics", "Event Booking topics"),
		ClickAnalyticsEvent.Null("Select Store", "Event Booking Store"));
}

async Task Step10_PostEventAnalytics(Context context) {
	// ---- Step 10: report the selected event. ----
	await PostAnalyticsAsync("10. post event analytics",
		ClickAnalyticsEvent.Null("Select Date", "Event Booking Date"),
		ClickAnalyticsEvent.Null("Select Topics", "Event Booking topics"),
		ClickAnalyticsEvent.Null("Select Store", "Event Booking Store"),
		ClickAnalyticsEvent.Click("Select Item Event Thumbnail", $"Event Booking: event selected ({context.SelectedEvent.Title})"),
		ClickAnalyticsEvent.Click("Select Event Thumbnail", "Event Booking: click/select thumbnail event"));
}

async Task Step12_PostBookingFormAnalytics(Context context) {
	// ---- Step 12: report the booking form fields. ----
	await PostAnalyticsAsync("12. post booking form analytics",
		ClickAnalyticsEvent.Click("Book Event Button Event Details", "Event Booking: book event button"),
		ClickAnalyticsEvent.Click("firstName", "First Name"),
		ClickAnalyticsEvent.Click("lastName", "Last Name"),
		ClickAnalyticsEvent.Click("email", "Email"),
		ClickAnalyticsEvent.Click("mobileNumber", "Phone number"),
		ClickAnalyticsEvent.Click("groupSize", "Group Size"));
}

async Task Step14_PostBookingCompletionAnalytics(Context context) {
	// ---- Step 14: report completion of the customer details form. ----
	await PostAnalyticsAsync("14. post booking completion analytics",
		ClickAnalyticsEvent.Click("Complete Button Customer Details", "Event Booking: customer details complete button"));
}


// Headers a Chrome browser adds to every request.
void AddBrowserHeaders(HttpRequestMessage request) {
	request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
	request.Headers.TryAddWithoutValidation("sec-ch-ua", context.SecChUa);
	request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
	request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
	request.Headers.TryAddWithoutValidation("User-Agent", context.UserAgent);
}

HttpRequestMessage JsonGet(string url) {
	var request = new HttpRequestMessage(HttpMethod.Get, url);
	AddBrowserHeaders(request);
	request.Headers.Referrer = new Uri(context.IndexUrl);
	request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
	request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
	request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
	request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
	return request;
}

HttpRequestMessage JsonPost(string url, string json) {
	var request = JsonGet(url);
	request.Method = HttpMethod.Post;
	request.Content = new StringContent(json, Encoding.UTF8, "application/json");
	request.Headers.TryAddWithoutValidation("Origin", context.BaseUrl);
	return request;
}

// Sends the request, prints one line, and returns the response body. Any non-2xx status throws.
async Task<string> SendAsync(string label, HttpRequestMessage request) {
	using var response = await http.SendAsync(request);
	string body = await response.Content.ReadAsStringAsync();
	Console.WriteLine($"{label}: {request.Method} {request.RequestUri!.AbsolutePath} -> {(int)response.StatusCode}");
	if (response.IsSuccessStatusCode)
		return body;

	string hint = (int)response.StatusCode >= 500 && response.Headers.TryGetValues("Retry-After", out var retryAfter)
		? $" (server is overloaded; it asks to retry after {retryAfter.First()} seconds)"
		: "";
	string excerpt = body.Length > 300 ? body[..300] + "..." : body;
	throw new InvalidOperationException($"{label} returned {(int)response.StatusCode} {response.StatusCode}{hint}: {excerpt}");
}

async Task PostAnalyticsAsync(string label, params ClickAnalyticsEvent[] events) {
	await SendOptionalAsync(label,
		JsonPost($"{context.BaseUrl}/event-series/{context.SeriesId}/session/{context.SessionId}/events",
			JsonSerializer.Serialize(events, JsonOptions)));
}

async Task SendOptionalAsync(string label, HttpRequestMessage request) {
	using var response = await http.SendAsync(request);
	string body = await response.Content.ReadAsStringAsync();
	Console.WriteLine($"{label}: {request.Method} {request.RequestUri!.AbsolutePath} -> {(int)response.StatusCode}");
	if (response.IsSuccessStatusCode)
		return;

	string excerpt = body.Length > 300 ? body[..300] + "..." : body;
	Console.Error.WriteLine($"   optional step failed: {(int)response.StatusCode} {response.StatusCode}: {excerpt}");
}

public sealed class UserConfig {
	public string SeriesId { get; set; } = "";
	public string Show { get; set; } = ""; // event selector
	public string FirstName { get; set; } = "";
	public string LastName { get; set; } = "";
	public string Email { get; set; } = "";
	public string Phone { get; set; } = "";
	public int GroupSize = 2;
	// retry-strategy)
}

public sealed class RunConfig {
	public bool Analytics { get; set; }
	public bool Submit { get; set; }
	public bool ListOnly { get; set; }

}

public sealed class WidgetSessionRegistrationRequest {
	[JsonPropertyName("userID")]
	public string? UserId { get; set; }
	public List<EventBookingSessionRequest> Sessions { get; set; } = [];
}

public sealed record ClickAnalyticsEvent(string action, ClickAnalyticsEventProperties properties) {
	public static ClickAnalyticsEvent Null(string action, string label) => new(action, new(label + ": undefined", null));
	public static ClickAnalyticsEvent Click(string action, string label) => new(action, new(label, "click"));
}

public sealed record ClickAnalyticsEventProperties(
	string label,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? eventType,
	string category = "Event");

public sealed class Context {
	// Known Constants
	public string BaseUrl => "https://bookings-us.qudini.com";
	public string Timezone { get; set; } = "America/New_York";
	public string Language { get; set; } = "en";

	// Browser Constants
	public string ChromeVersion => "149";
	public string UserAgent => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromeVersion}.0.0.0 Safari/537.36";
	public string SecChUa => $"\"Chromium\";v=\"{ChromeVersion}\", \"Google Chrome\";v=\"{ChromeVersion}\", \"Not/A)Brand\";v=\"99\"";

	// HTTP objects
	public HttpClient Http { get; set; } = null!;
	public CookieContainer CookieJar { get; set; } = new();

	// ------- config / options to select desired event ------
	public string? EventIdentifier { get; set; }
	public string? TitleText { get; set; }
	public string Show { get; set; } = "";

	// User Properties
	public string FirstName { get; set; } = "Test";
	public string LastName { get; set; } = "Dummy";
	public string Email { get; set; } = "test.dummy@example.com";
	public string MobileNumber { get; set; } = "+15135551212";
	public int GroupSize { get; set; } = 1;

	// ---- Series Details ----
	public string SeriesId { get; set; } = "";
	public SeriesSettings? SeriesSettings { get; set; }
	public string IndexUrl => $"{BaseUrl}/booking-widget/events/{SeriesId}"; // helper

	// ---- Booking Session ----
	public string? UserId => GetCookie("Q-BW-USER-ID");
	public string? SessionId => GetCookie("Q-BW-SESSION-ID");

	// --------
	public List<QudiniEvent> Events { get; set; } = [];
	public QudiniEvent? SelectedEvent { get; set; }

	public string? BookingReference { get; set; }

	public void InitializeFrom(UserConfig config) {
		Console.WriteLine($"Using config:\r\n\tseries: {config.SeriesId},\r\n\tshow: \"{config.Show}\",\r\n\tattendee: {config.FirstName} {config.LastName},\r\n\temail: {config.Email},\r\n\tphone: {config.Phone},\r\n\tgroup size: {config.GroupSize}\r\n");
		SeriesId = config.SeriesId;
		Show = config.Show;
		FirstName = config.FirstName;
		LastName = config.LastName;
		Email = config.Email;
		MobileNumber = config.Phone;
		GroupSize = config.GroupSize;
	}

	// helper methods
	public int GetGroupSizeToRequest() {
		QudiniEvent selectedEvent = SelectedEvent
			?? throw new InvalidOperationException("No event has been selected.");
		int effectiveMaxGroupSize = Math.Min(selectedEvent.SlotsAvailable, selectedEvent.MaxGroupSize);
		if (effectiveMaxGroupSize < GroupSize)
			Console.WriteLine($"   WARNING: group size {GroupSize} reduced to fit (seats {selectedEvent.SlotsAvailable}, max group {selectedEvent.MaxGroupSize}); the booking will likely be refused.");

		return Math.Min(GroupSize, effectiveMaxGroupSize);
	}

	public string GetBookingJson() {
		BookingRequest bookingRequest = new BookingRequest {
			FirstName = FirstName,
			LastName = LastName,
			Email = Email,
			MobileNumber = SeriesSettings?.PhoneIsVisible == true ? MobileNumber : null,
			GroupSize = GetGroupSizeToRequest(),
			EventId = SelectedEvent?.Id ?? throw new InvalidOperationException("No event has been selected."),
			Timezone = Timezone,
			Attribution = SeriesSettings?.DefaultAttributionQuestion
				?? throw new InvalidOperationException("Series settings are not loaded."),
			Language = Language,
		};
		return JsonSerializer.Serialize(bookingRequest, new JsonSerializerOptions {
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		});
	}

	string GetCookie(string name) {
		return CookieJar.GetCookies(new Uri(BaseUrl))[name]?.Value
			?? throw new InvalidOperationException($"The booking page did not set the {name} cookie.");
	}

}

public sealed class SeriesSettings {
	[JsonPropertyName("attributionQuestions")]
	public List<string> AttributionQuestions { get; set; } = [];

	[JsonPropertyName("phoneNumberState")]
	public string PhoneNumberState { get; set; } = "";

	[JsonIgnore]
	public string DefaultAttributionQuestion => AttributionQuestions.FirstOrDefault()
		?? throw new InvalidOperationException("Series settings had no attribution answer.");

	[JsonIgnore]
	public bool PhoneIsVisible => PhoneNumberState != "HIDDEN";
}

public sealed class QudiniEvent {
	[JsonPropertyName("id")]
	public int Id { get; set; }

	[JsonPropertyName("identifier")]
	public string Identifier { get; set; } = "";

	[JsonPropertyName("startISO")]
	public string StartIso { get; set; } = "";

	[JsonPropertyName("slotsAvailable")]
	public int SlotsAvailable { get; set; }

	[JsonPropertyName("maxGroupSize")]
	public int MaxGroupSize { get; set; }

	[JsonPropertyName("title")]
	public string Title { get; set; } = "";

	[JsonPropertyName("hasPassed")]
	public bool HasPassed { get; set; }

	// Other SNL Properties not used:
	// startDate   (human friendly string)
	// startTime   (human friendly string)
	// durationMinutes (int)
	// shop (storeName, address1, address2, locality, region, postalcode, timeZone, latitude, longitude, googleMapLink)
	// seriesId  (int)
	// locationName  (null)
}

public sealed class EventBookingSessionRequest {
	[JsonPropertyName("path")]
	public string? Path { get; set; }
	[JsonPropertyName("action")]
	public string? Action { get; set; }
	[JsonPropertyName("properties")]
	public object[] Properties { get; set; } = [];
	[JsonPropertyName("sessionID")]
	public string? SessionId { get; set; }
	public string Device { get; set; } = "unknown";
	public string Os { get; set; } = "windows";
	public string OsVersion { get; set; } = "windows-10";
	public string Browser { get; set; } = "chrome";
	public string BrowserVersion { get; set; } = "";
	public string Referrer { get; set; } = "";
	[JsonPropertyName("kioskIdentifier")]
	public string? KioskIdentifier { get; set; }
	[JsonPropertyName("userID")]
	public string? UserId { get; set; }
}

public sealed class BookingRequest {
	[JsonPropertyName("firstName")]
	public string FirstName { get; set; } = "";
	[JsonPropertyName("lastName")]
	public string LastName { get; set; } = "";
	[JsonPropertyName("email")]
	public string Email { get; set; } = "";
	[JsonPropertyName("mobileNumber")]
	public string? MobileNumber { get; set; }
	[JsonPropertyName("groupSize")]
	public int GroupSize { get; set; }
	[JsonPropertyName("eventId")]
	public int EventId { get; set; }
	public string Timezone { get; set; } = "";
	public string Attribution { get; set; } = "";
	public string Language { get; set; } = "";
}

public sealed class BookingResponse {
	[JsonPropertyName("refNumber")]
	public string? ReferenceNumber { get; set; }
}
