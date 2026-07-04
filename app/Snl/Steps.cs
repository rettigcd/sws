using System.Text.Json;
using AngleSharp;

namespace Snl;

internal static class Steps {

	/// <returns>
	/// <see cref="StepResult.IndexPageRetrieved"/> if the endpoint returned a successful HTML response, populating
	/// <see cref="Context.JavascriptScripts"/>, <see cref="Context.CurrentPage"/>, <see cref="Context.BwSessionId"/>,
	/// and <see cref="Context.BwUserId"/>.
	/// </returns>
	public static async Task<StepResult> GetIndexPageAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		var requestUri = $"https://bookings-us.qudini.com/booking-widget/events/{seriesId}/event/choose";
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

		var html = await response.Content.ReadAsStringAsync();

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

	/// <returns>
	/// <see cref="StepResult.ScriptRequested"/> once the script resource returns a successful response; the response body is discarded.
	/// </returns>
	public static async Task<StepResult> RequestGenericAsync(Context ctx, string uri) {
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

	/// <summary>Sets the standard headers used by the JSON GET endpoints (Accept, Sec-Fetch-*).</summary>
	static void AddJsonGetHeaders(HttpRequestMessage request) {
		request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
	}

	/// <returns>
	/// <see cref="StepResult.ConfigRetrieved"/> once the series config endpoint returns a successful response.
	/// </returns>
	public static async Task<StepResult> GetSeriesConfigAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		var requestUri = $"https://bookings-us.qudini.com/booking-widget/event/series/{seriesId}";
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
	/// <see cref="StepResult.EventsRetrieved"/> once the series events endpoint returns a successful response,
	/// populating <see cref="Context.Events"/> with metadata for each event returned.
	/// </returns>
	public static async Task<StepResult> GetSeriesEventsAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		var requestUri = $"https://bookings-us.qudini.com/booking-widget/event/events/{seriesId}";
		var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
		if (ctx.CurrentPage is not null)
			request.Headers.Referrer = new Uri(ctx.CurrentPage);

		AddJsonGetHeaders(request);

		var response = await http.SendAsync(request);
		if (!response.IsSuccessStatusCode)
			throw new InvalidOperationException($"Series events endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		var responseJson = await response.Content.ReadAsStringAsync();
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

	/// <summary>The event this session is booking - the first event returned by GetSeriesEventsAsync.</summary>
	static EventMetadata GetSelectedEvent(Context ctx) =>
		ctx.Events.Count > 0 ? ctx.Events[0] : throw new InvalidOperationException("Context.Events is not set.");

	/// <returns>
	/// <see cref="StepResult.LanguageOptionsRetrieved"/> once the series languages endpoint returns a successful response.
	/// </returns>
	public static async Task<StepResult> GetSeriesLanguagesAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		var requestUri = $"https://bookings-us.qudini.com/series-languages/{seriesId}";
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
		var seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");

		var requestUri = $"https://bookings-us.qudini.com/series-translation/en/{seriesId}";
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
	/// <paramref name="successResult"/> once the events endpoint returns a successful response.
	/// <see cref="StepResult.SessionNotFound"/> if the response JSON has "status": 404.
	/// </returns>
	static async Task<StepResult> PostSessionAnalyticsEventsAsync(Context ctx, string json, StepResult successResult, string endpointLabel) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");
		var bwSessionId = ctx.BwSessionId ?? throw new InvalidOperationException("Context.BwSessionId is not set.");

		var requestUri = $"https://bookings-us.qudini.com/event-series/{seriesId}/session/{bwSessionId}/events";
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
			throw new InvalidOperationException($"{endpointLabel} endpoint returned {(int)response.StatusCode} {response.StatusCode}.");

		var responseJson = await response.Content.ReadAsStringAsync();
		using var document = JsonDocument.Parse(responseJson);
		if (document.RootElement.TryGetProperty("status", out var status) && status.GetInt32() == 404)
			return StepResult.SessionNotFound;

		return successResult;
	}

	/// <returns>
	/// <see cref="StepResult.UiInteractionEventsSubmitted"/> once the events endpoint returns a successful response.
	/// <see cref="StepResult.SessionNotFound"/> if the response JSON has "status": 404.
	/// </returns>
	public static Task<StepResult> SubmitUiInteractionEventsAsync(Context ctx) {
		const string json = """
			[
				{"action":"Select Date","properties":{"label":"Event Booking Date: undefined","category":"Event"}},
				{"action":"Select Topics","properties":{"label":"Event Booking topics: undefined","category":"Event"}},
				{"action":"Select Store","properties":{"label":"Event Booking Store: undefined","category":"Event"}}
			]
			""";

		return PostSessionAnalyticsEventsAsync(ctx, json, StepResult.UiInteractionEventsSubmitted, "UI interaction events");
	}

	/// <returns>
	/// <see cref="StepResult.EventBookingSessionCreated"/> once the event session endpoint returns a successful response.
	/// </returns>
	public static async Task<StepResult> CreateEventBookingSessionAsync(Context ctx) {
		var http = ctx.Http ?? throw new InvalidOperationException("Context.Http is not set.");
		var seriesId = ctx.SeriesId ?? throw new InvalidOperationException("Context.SeriesId is not set.");
		var eventId = GetSelectedEvent(ctx).Identifier;
		var bwSessionId = ctx.BwSessionId ?? throw new InvalidOperationException("Context.BwSessionId is not set.");
		var bwUserId = ctx.BwUserId ?? throw new InvalidOperationException("Context.BwUserId is not set.");

		var requestUri = $"https://bookings-us.qudini.com/event-series/{seriesId}/events/{eventId}/session";
		var json = JsonSerializer.Serialize(new {
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
	static object BuildItemEventThumbnailSelectedEvent(string eventTitle) => new {
		action = "Select Item Event Thumbnail",
		properties = new {
			label = $"Event Booking: event selected ({eventTitle})",
			eventType = "click",
			category = "Event",
		},
	};

	/// <returns>
	/// <see cref="StepResult.EventThumbnailSelectedAnalyticsSubmitted"/> once the events endpoint returns a successful response.
	/// <see cref="StepResult.SessionNotFound"/> if the response JSON has "status": 404.
	/// </returns>
	public static Task<StepResult> SubmitEventThumbnailSelectedAnalyticsAsync(Context ctx) {
		var eventTitle = GetSelectedEvent(ctx).Title;

		var json = JsonSerializer.Serialize(new[] { BuildItemEventThumbnailSelectedEvent(eventTitle) });

		return PostSessionAnalyticsEventsAsync(ctx, json, StepResult.EventThumbnailSelectedAnalyticsSubmitted, "Event thumbnail selected analytics");
	}

	/// <returns>
	/// <see cref="StepResult.EventThumbnailClickedAnalyticsSubmitted"/> once the events endpoint returns a successful response.
	/// <see cref="StepResult.SessionNotFound"/> if the response JSON has "status": 404.
	/// </returns>
	public static Task<StepResult> SubmitEventThumbnailClickedAnalyticsAsync(Context ctx) {
		var eventTitle = GetSelectedEvent(ctx).Title;

		var json = JsonSerializer.Serialize(new[] {
			BuildItemEventThumbnailSelectedEvent(eventTitle),
			new {
				action = "Select Event Thumbnail",
				properties = new {
					label = "Event Booking: click/select thumbnail event",
					eventType = "click",
					category = "Event",
				},
			},
		});

		return PostSessionAnalyticsEventsAsync(ctx, json, StepResult.EventThumbnailClickedAnalyticsSubmitted, "Event thumbnail clicked analytics");
	}

}
