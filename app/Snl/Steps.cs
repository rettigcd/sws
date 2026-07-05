using System.Text.Json;
using AngleSharp;

namespace Snl;

internal static class Steps {

	// !!! cleanup headers - maybe remove ones that are not required
	// - kkep Accept, Origin, Referer

	/// <returns>
	/// <see cref="StepResult.IndexPageRetrieved"/> if the endpoint returned a successful HTML response, populating
	/// <see cref="Context.JavascriptScripts"/>, <see cref="Context.CurrentPage"/>, <see cref="Context.BwSessionId"/>,
	/// and <see cref="Context.BwUserId"/>.
	/// </returns>
	public static async Task<StepResult> GetIndexPageAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		string seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		// Some sessions may append "/event/choose" to end of Uri but that can be discard and ignored.
		string requestUri = $"https://bookings-us.qudini.com/booking-widget/events/{seriesId}";
		var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
		request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
		request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
		request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
		request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Google Chrome\";v=\"149\", \"Chromium\";v=\"149\", \"Not)A;Brand\";v=\"24\"");
		request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
		request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
		request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36");

		var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Index page endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		string html = await response.Content.ReadAsStringAsync();

		var browsingContext = BrowsingContext.New(Configuration.Default);
		var document = await browsingContext.OpenAsync(req => req.Content(html).Address(requestUri)).ConfigureAwait(false);
		var baseUri = new Uri(requestUri);

		var scriptUris = document.QuerySelectorAll("script[src]")
			.Select(script => script.GetAttribute("src"))
			.Where(src => !string.IsNullOrWhiteSpace(src))
			.Select(src => new Uri(baseUri, src!).ToString())
			.ToList();

		if (scriptUris.Count == 0)
			throw new InvalidOperationException("Index page did not contain any <script src> tags.");

		ctx.JavascriptScripts.AddRange(scriptUris);
		ctx.CurrentPage = requestUri;

		ctx.BwSessionId = http.Cookies.GetCookies(baseUri)["Q-BW-SESSION-ID"]?.Value
			?? throw new InvalidOperationException("Response did not set the Q-BW-SESSION-ID cookie.");

		ctx.BwUserId = http.Cookies.GetCookies(baseUri)["Q-BW-USER-ID"]?.Value
			?? throw new InvalidOperationException("Response did not set the Q-BW-USER-ID cookie.");

		return StepResult.IndexPageRetrieved;
	}


	public static async Task<StepResult> RequestScriptAsync(Context ctx, string uri) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var request = new HttpRequestMessage(HttpMethod.Get, uri);
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);
		request.Headers.TryAddWithoutValidation("Accept", "*/*");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "script");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
		using var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Script endpoint returned {(int)response.StatusCode} {response.StatusCode}.");
		return StepResult.ScriptRequested;
	}

	public static async Task<StepResult> RequestTemplateAsync(Context ctx, string uri) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var request = new HttpRequestMessage(HttpMethod.Get, uri);
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);
		request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
		using var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Template endpoint returned {(int)response.StatusCode} {response.StatusCode}.");
		return StepResult.ScriptRequested;
	}

	public static async Task<StepResult> RequestJsonResourceAsync(Context ctx, string uri) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var request = new HttpRequestMessage(HttpMethod.Get, uri);
		request.Headers.Referrer = new Uri("https://bookings-us.qudini.com/");
		request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
		request.Headers.TryAddWithoutValidation("Origin", "https://bookings-us.qudini.com");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
		using var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"JSON resource endpoint returned {(int)response.StatusCode} {response.StatusCode}.");
		return StepResult.ScriptRequested;
	}

	/// <summary>Sets the standard headers used by the JSON GET endpoints (Accept, Sec-Fetch-*).</summary>
	static void AddJsonGetHeaders(HttpRequestMessage request) {
		request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
	}

	/// <returns>
	/// <see cref="StepResult.WidgetSessionRegistered"/> once the widget session registration endpoint returns a successful response.
	/// </returns>
	public static async Task<StepResult> RegisterWidgetSessionAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		string seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");
		string bwSessionId = ctx.BwSessionId ?? throw new InvalidOperationException("Context.BwSessionId is not set.");
		string bwUserId = ctx.BwUserId ?? throw new InvalidOperationException("Context.BwUserId is not set.");

		string requestUri = $"https://bookings-us.qudini.com/event-series/{seriesId}/session";
		string json = JsonSerializer.Serialize(new {
			userID = bwUserId,
			sessions = new[] {
				new {
					path = (string?)null,
					action = (string?)null,
					properties = Array.Empty<object>(),
					sessionID = bwSessionId,
					device = "unknown",
					os = "windows",
					osVersion = "windows-10",
					browser = "chrome",
					browserVersion = "149.0.0.0",
					referrer = ctx.CurrentPage ?? "",
					kioskIdentifier = (string?)null,
				}
			},
		});

		var request = new HttpRequestMessage(HttpMethod.Post, requestUri) {
			Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
		};
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
		request.Headers.TryAddWithoutValidation("Origin", "https://bookings-us.qudini.com");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");

		var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Widget session registration endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		return StepResult.WidgetSessionRegistered;
	}

	/// <returns>
	/// <see cref="StepResult.ConfigRetrieved"/> once the series config endpoint returns a successful response.
	/// </returns>
	public static async Task<StepResult> GetSeries_ConfigAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		string seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		string requestUri = $"https://bookings-us.qudini.com/booking-widget/event/series/{seriesId}";
		var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		AddJsonGetHeaders(request);

		using var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Series config endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		// TODO: parse the response and populate Context once the shape it's needed for is known.

		return StepResult.ConfigRetrieved;
	}

	/// <returns>
	/// <see cref="StepResult.TopicsRetrieved"/> once the series topics endpoint returns a successful response.
	/// </returns>
	public static async Task<StepResult> GetSeries_TopicsAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		string seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		string requestUri = $"https://bookings-us.qudini.com/booking-widget/event/topics/{seriesId}";
		var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		AddJsonGetHeaders(request);

		using var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Series topics endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		return StepResult.TopicsRetrieved;
	}

	/// <returns>
	/// <see cref="StepResult.VenuesRetrieved"/> once the series venues endpoint returns a successful response.
	/// </returns>
	public static async Task<StepResult> GetSeries_VenuesAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		string seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		string requestUri = $"https://bookings-us.qudini.com/booking-widget/event/venues/{seriesId}";
		var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		AddJsonGetHeaders(request);

		using var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Series venues endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		return StepResult.VenuesRetrieved;
	}

	/// <returns>
	/// <see cref="StepResult.EventsRetrieved"/> once the series events endpoint returns a successful response,
	/// populating <see cref="Context.Events"/> with metadata for each event returned.
	/// </returns>
	public static async Task<StepResult> GetSeriesEventsAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		string seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		string requestUri = $"https://bookings-us.qudini.com/booking-widget/event/events/{seriesId}";
		var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		AddJsonGetHeaders(request);

		var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Series events endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		string responseJson = await response.Content.ReadAsStringAsync();
		using var document = JsonDocument.Parse(responseJson);
		var events = document.RootElement;
		if (events.ValueKind != JsonValueKind.Array || events.GetArrayLength() == 0)
			throw new InvalidOperationException("Series events endpoint did not return any events.");

		ctx.Events.AddRange(events.EnumerateArray().Select(ParseEventMetadata));

		return StepResult.EventsRetrieved;
	}

	static EventMetadata ParseEventMetadata(JsonElement e) {
		var shop = e.GetProperty("shop");
		return new EventMetadata(
			Id: e.GetProperty("id").GetInt32(),
			Identifier: e.GetProperty("identifier").GetString() ?? throw new InvalidOperationException("Event did not have an identifier."),
			Title: e.GetProperty("title").GetString() ?? throw new InvalidOperationException("Event did not have a title."),
			StartIso: e.GetProperty("startISO").GetString() ?? throw new InvalidOperationException("Event did not have a startISO."),
			SlotsAvailable: e.GetProperty("slotsAvailable").GetInt32(),
			MaxGroupSize: e.GetProperty("maxGroupSize").GetInt32(),
			HasPassed: e.GetProperty("hasPassed").GetBoolean(),
			StoreName: shop.GetProperty("storeName").GetString() ?? throw new InvalidOperationException("Event's shop did not have a storeName."),
			StoreIdentifier: shop.GetProperty("storeIdentifier").GetString() ?? throw new InvalidOperationException("Event's shop did not have a storeIdentifier.")
		);
	}

	/// <returns>
	/// <see cref="StepResult.LanguageOptionsRetrieved"/> once the series languages endpoint returns a successful response.
	/// </returns>
	public static async Task<StepResult> GetSeriesLanguagesAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		string seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		string requestUri = $"https://bookings-us.qudini.com/series-languages/{seriesId}";
		var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		AddJsonGetHeaders(request);

		using var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Series languages endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		// TODO: parse the response and populate Context once the shape it's needed for is known.

		return StepResult.LanguageOptionsRetrieved;
	}

	/// <returns>
	/// <see cref="StepResult.LanguageDictRetrieved"/> once the series translation endpoint returns a successful response.
	/// </returns>
	public static async Task<StepResult> GetSeriesTranslationAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		string seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		string requestUri = $"https://bookings-us.qudini.com/series-translation/en/{seriesId}";
		var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		AddJsonGetHeaders(request);

		using var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Series translation endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		// TODO: parse the response and populate Context once the shape it's needed for is known.

		return StepResult.LanguageDictRetrieved;
	}

	/// <summary>
	/// Shared plumbing for the fire-and-forget analytics endpoint (POST .../event-series/{seriesId}/session/{bwSessionId}/events).
	/// The response may be {"status":404,"message":"Session not found"} - this is not part of the booking-critical path.
	/// </summary>
	/// <returns>
	/// <see cref="StepResult.AnalyticsSubmitted"/> once the events endpoint returns a successful response.
	/// <see cref="StepResult.SessionNotFound"/> if the response JSON has "status": 404.
	/// </returns>
	public static async Task<StepResult> PostSessionAnalyticsEventsAsync(Context ctx, params ClickAnalyticsEvent[] events) {
		string json = JsonSerializer.Serialize(events);
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		string seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");
		string bwSessionId = ctx.BwSessionId ?? throw new InvalidOperationException("Context.BwSessionId is not set.");

		string requestUri = $"https://bookings-us.qudini.com/event-series/{seriesId}/session/{bwSessionId}/events";
		var request = new HttpRequestMessage(HttpMethod.Post, requestUri) {
			Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
		};
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
		request.Headers.TryAddWithoutValidation("Origin", "https://bookings-us.qudini.com");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");

		var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Event thumbnail clicked analytics endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		string responseJson = await response.Content.ReadAsStringAsync();
		using var document = JsonDocument.Parse(responseJson);
		if (document.RootElement.TryGetProperty("status", out var status) && status.GetInt32() == 404)
			return StepResult.SessionNotFound;

		return StepResult.AnalyticsSubmitted;
	}

	/// <returns>
	/// <see cref="StepResult.EventBookingSessionCreated"/> once the event session endpoint returns a successful response.
	/// </returns>
	public static async Task<StepResult> CreateEventBookingSessionAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		string seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");
		string eventId = (ctx.SelectedEvent ?? throw new InvalidOperationException("Context.SelectedEvent is not set.")).Identifier;
		string bwSessionId = ctx.BwSessionId ?? throw new InvalidOperationException("Context.BwSessionId is not set.");
		string bwUserId = ctx.BwUserId ?? throw new InvalidOperationException("Context.BwUserId is not set.");

		string requestUri = $"https://bookings-us.qudini.com/event-series/{seriesId}/events/{eventId}/session";
		string json = JsonSerializer.Serialize(new {
			path = (string?)null,
			action = (string?)null,
			properties = Array.Empty<object>(),
			sessionID = bwSessionId,
			device = "unknown",
			os = "windows",
			osVersion = "windows-10",
			browser = "chrome",
			browserVersion = "149.0.0.0",
			referrer = "",
			kioskIdentifier = (string?)null,
			userID = bwUserId,
		});

		var request = new HttpRequestMessage(HttpMethod.Post, requestUri) {
			Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
		};
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
		request.Headers.TryAddWithoutValidation("Origin", "https://bookings-us.qudini.com");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");

		var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Event booking session endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		return StepResult.EventBookingSessionCreated;
	}

	/// <summary>Builds the "Select Item Event Thumbnail" analytics event, labeled with the given event title.</summary>
	public static ClickAnalyticsEvent BuildItemEventThumbnailSelectedEvent(Context ctx) {
		string eventTitle = (ctx.SelectedEvent ?? throw new InvalidOperationException("Context.SelectedEvent is not set.")).Title;
		return ClickAnalyticsEvent.Click("Select Item Event Thumbnail", $"Event Booking: event selected ({eventTitle})");
	}

	/// <returns>
	/// <see cref="StepResult.BookingCreated"/> once the booking endpoint returns a successful response, populating
	/// <see cref="Context.BookingReferenceNumber"/>.
	/// </returns>
	public static async Task<StepResult> CreateBookingAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		string seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");
		int eventId = (ctx.SelectedEvent ?? throw new InvalidOperationException("Context.SelectedEvent is not set.")).Id;
		var user = ctx.User ?? throw new InvalidOperationException("Context.User is not set.");
		string firstName = user.FirstName ?? throw new InvalidOperationException("UserInfo.FirstName is not set.");
		string lastName = user.LastName ?? throw new InvalidOperationException("UserInfo.LastName is not set.");
		string email = user.Email ?? throw new InvalidOperationException("UserInfo.Email is not set.");
		string mobileNumber = user.MobileNumber ?? throw new InvalidOperationException("UserInfo.MobileNumber is not set.");

		string requestUri = $"https://bookings-us.qudini.com/booking-widget/series/{seriesId}/event/book";
		string json = JsonSerializer.Serialize(new {
			firstName,
			lastName,
			email,
			mobileNumber,
			groupSize = ctx.GroupSize,
			eventId,
			timezone = "America/New_York",
			attribution = "No answer",
			language = "en",
		});

		var request = new HttpRequestMessage(HttpMethod.Post, requestUri) {
			Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
		};
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
		request.Headers.TryAddWithoutValidation("Origin", "https://bookings-us.qudini.com");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");

		var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Booking endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		string responseJson = await response.Content.ReadAsStringAsync();
		using var document = JsonDocument.Parse(responseJson);
		ctx.BookingReferenceNumber = document.RootElement.GetProperty("refNumber").GetString()
			?? throw new InvalidOperationException("Booking endpoint response did not have a refNumber.");

		return StepResult.BookingCreated;
	}

}
