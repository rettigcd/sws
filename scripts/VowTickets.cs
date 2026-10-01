#:property PublishAot=false
// Signs up for SNL Standby tickets through the vow.app registration site (which replaced the Qudini booking widget).
// It sends the same API calls a browser sends: the steps that are known to be needed, plus (with --analytics) browser-mimicking steps
// that are NOT proven required. Which steps are needed was tested on 2026-09-24 against a full event; see "What was tested" below.
// Everything here is based on the captures saz/snl_sep_24/snl_sep_24_part_1.saz and snl_sep_24_part_2.saz,
// documented in docs/VOW_SNL_FLOW.md (read it first; section numbers below refer to it).
//
// Run:      dotnet run scripts/VowTickets.cs -- [--config INDEX | --site snl] [--analytics] [--submit] [--list]
//                                               [--title TEXT] [--event UUID] [--group-size N] [--series SLUG]
// Default:  a dry run. The read-only steps are sent (list events, load the journey), then the RSVP request is printed but NOT sent.
//           Add --submit to send it.
// --list:   print every event (uuid, start, status, seats, name) after step 1 and stop. Skips the wait for the run time.
// Choosing: --event picks by uuid; --title picks the first upcoming event whose name contains TEXT (any case);
//           with neither, the first event that is open, has seats and has not passed is used.
// --analytics: also send the calls a browser makes that are not proven to be required (page view, auth check, Pusher WebSocket for
//           the X-Socket-ID header, log-interaction). Skipping them is untested against an event that has room (see below); when a real
//           registration is at stake and you want to copy the browser as closely as possible, pass --analytics.
// --series: the vow.app "by-url" slug that lists the events. SNL is "nbc". (Qudini's series id has no equivalent.)
// Note:     --submit registers with the data in the config and takes a real spot. Use it only when that is what you want.
//
// What was tested (2026-09-24, both events full, RSVP sent with placeholder data; docs/VOW_SNL_FLOW.md 7.6):
//   The RSVP got the same answer, 422 "This event is full", with: no --analytics; --analytics; no cookies at all; a made-up X-Socket-ID;
//   and no Origin/Referer. So none of those is checked BEFORE the capacity check. This does not show they are unchecked for an event with room.
//   A browser User-Agent IS required: Cloudflare answers any request with a script's default User-Agent with 403 "Error 1010" (all api.vow.app endpoints).
//
// !!! The success response of the RSVP call was never captured (both shows were full). Step 8 treats any 2xx as success and prints the
// !!! body. Everything about the success path is inferred from the site's JavaScript. Verify against a capture of an open show.

// ==== TODO ====
// Confirm the RSVP success response shape, and then read the booking/confirmation number from it.
// Confirm whether X-Socket-ID / Pusher / log-interaction / cookies / Origin are required once an event has room (untested; all skipped without --analytics or not enforced when full).
// Find the rate-limit window (x-ratelimit-limit is 10 per window; see docs/VOW_SNL_FLOW.md 5.4).

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var JsonOptions = new JsonSerializerOptions {
	PropertyNameCaseInsensitive = true,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

const string ForSNL = "forSNL";

// The user data below is placeholder data. Replace it before using --submit.
UserConfig[] configs = [
	// SNL
	new UserConfig {
		Series = Vow.SnlSlug,
		Show = "dress",				// event selector (part of the event name)
		FirstName = "Test",
		LastName = "Dummy",
		Email = "test.dummy@example.com",
		GroupSize = 1,
		RunAt = ForSNL,
	},
];

var context = new Context();
var runConfig = new RunConfig();
Func<VowEvent, bool> available = e => e.IsOpen && e.SlotsAvailable > 0 && !e.HasPassed;
Func<VowEvent, bool> selector = available;
DateTime runTime = DateTime.Now;
DateTime scriptStart = DateTime.Now;

const string Usage = "Usage: dotnet run scripts/VowTickets.cs -- --config INDEX|--site snl [--analytics] [--submit] [--list] [--title TEXT] [--event UUID] [--group-size N] [--series SLUG]";
for (int i = 0; i < args.Length; i++) {
	switch (args[i]) {

		// series selection - selects which vow.app event list to read.
		case "--config" when i + 1 < args.Length: {
			if (!int.TryParse(args[++i], out int configIndex) || configIndex < 0 || configIndex >= configs.Length) {
				Console.Error.WriteLine($"Unknown config index. Use a value from 0 to {configs.Length - 1}.");
				Console.Error.WriteLine(Usage);
				return 2;
			}
			var config = configs[configIndex];
			// User Info / Group Size
			context.InitializeFrom(config);
			// Show Selector
			selector = e => e.Name.Contains(context.Show, StringComparison.OrdinalIgnoreCase) && !e.HasPassed;
			// Run At
			if (config.RunAt == ForSNL)
				runTime = GetNextThursday10Am(500);
			break;
		}
		case "--site" when i + 1 < args.Length: {
			string siteName = args[++i].ToLowerInvariant();
			context.Series = siteName switch { "snl" => Vow.SnlSlug, _ => "" };
			break;
		}
		case "--series" when i + 1 < args.Length: context.Series = args[++i]; break;

		// event selection
		case "--event" when i + 1 < args.Length: {
			string eventUuid = args[++i];
			selector = e => string.Equals(e.Uuid, eventUuid, StringComparison.OrdinalIgnoreCase);
			break;
		}
		case "--title" when i + 1 < args.Length: {
			string titleText = args[++i];
			selector = e => e.Name.Contains(titleText, StringComparison.OrdinalIgnoreCase) && !e.HasPassed;
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

if (context.Series == "") {
	Console.Error.WriteLine("Missing or unknown --site (use snl).");
	Console.Error.WriteLine("OR --series [slug].");
	Console.Error.WriteLine(Usage);
	return 2;
}

// ---- wait ----
if (!runConfig.ListOnly && !WaitUntilRunTime())
	return 0;

using var handler = new HttpClientHandler {
	CookieContainer = context.CookieJar,
	UseCookies = true,
	AutomaticDecompression = DecompressionMethods.All,
};
using var http = new HttpClient(handler);
context.Http = http;

var logItems = new List<string>();
void LogLn(string s){ logItems.Add(s); logItems.Add("\r\n"); }
LogLn($"{scriptStart:yyyy-MM-dd HH:mm:ss.fff} SCRIPT STARTED");

try {
	// 1. Which events exist, and are they open? (required)
	await Step1_GetEventsList(context);

	// ---- Exit Ramp ----
	if (runConfig.ListOnly) {
		foreach (var e in context.Events)
			Console.WriteLine($"   {e.Uuid} Starts At:{DateTimeOffset.Parse(e.StartsAt):yyyy-MM-dd HH:mm:ss}  {e.Status,-12} {e.SlotsAvailable,4} seats  {e.Name}");
		return 0;
	}

	// Select event.
	context.SelectedEvent
		= context.Events.Where(selector).FirstOrDefault()	// the one we want
		?? context.Events.Where(available).FirstOrDefault()	// fallback if desired one is unavailable
		?? throw new InvalidOperationException("No matching event is open with seats available. Use --list to see the events.");
	Console.ForegroundColor = ConsoleColor.Green;
	Console.Write($"   Selected Event: \"{context.SelectedEvent.Name}\" on {context.SelectedEvent.StartsAt}, status {context.SelectedEvent.Status}, {context.SelectedEvent.SlotsAvailable} seats ");
	Console.ResetColor();
	Console.WriteLine($"(uuid {context.SelectedEvent.Uuid} journey {context.SelectedEvent.JourneyId})");

	// 2-3. What the browser does when the registration page opens. (browser-mimicking; not proven required)
	if (runConfig.Analytics) {
		await Step2_OpenRegistrationPage(context);
		await Step3_CheckAuthUser(context);
	}

	// 4. Load the journey: sets the cookies and gives the step/action ids and the guest limit. (required)
	await Step4_LoadJourney(context);

	// 5-7. Pusher connection and the "visitor clicked through the landing page" calls. (browser-mimicking; not proven required)
	if (runConfig.Analytics) {
		await Step5_ConnectPusher(context);
		await Step6_LogLandingView(context);
		await Step7_LogContinueClick(context);
	}

	// ---- Exit Ramp ----
	if (!runConfig.Submit) {
		Console.Write("8. submit RSVP (dry run, NOT sent; ");
		Console.ForegroundColor = ConsoleColor.Red;
		Console.Write("pass --submit to send");
		Console.ResetColor();
		Console.WriteLine("):");
		Console.WriteLine($"   PUT {Vow.ApiBase}/api/v2/events/{context.SelectedEvent.Uuid}/attendees/rsvp");
		Console.WriteLine($"   {context.GetRsvpJson()}");
		return 0;
	}

	// 8. Register. (required)
	await Step8_SubmitRsvp(context);

	// 9. Tell the server which step we ended on. (browser-mimicking; not proven required)
	if (runConfig.Analytics)
		await Step9_LogRsvpResult(context);

	return 0;
}
catch (Exception ex) {
	Console.Error.WriteLine($"FAILED: {ex.Message}");
	return 1;
}
finally {
	context.Pusher?.Dispose();

	LogLn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} SCRIPT COMPLETE");
	string logPath = $"vow_{scriptStart:yyyy-MM-dd_HH-mm-ss}.log";
	lock (logItems)
		File.WriteAllText(logPath, string.Concat(logItems));
	Console.WriteLine($"Log written to {Path.GetFullPath(logPath)}");
}

// ==================================
// ======= Wait until Run Time ======
// ==================================
bool WaitUntilRunTime() {

	var now = DateTime.Now;
	if (runTime <= now) return true;

	if (!runConfig.Submit) {
		Console.ForegroundColor = ConsoleColor.Red;
		Console.Write("WARNING: The RSVP will NOT be submitted.");
		Console.ResetColor();
		Console.WriteLine(" Use --submit to ensure submission.\r\n");
	}

	int stopRequested = 0;
	Console.CancelKeyPress += (_, eventArgs) => {
		eventArgs.Cancel = true;
		Interlocked.Exchange(ref stopRequested, 1);
		Console.WriteLine("\nCancelled. Exiting.");
	};

	var redTimeSpan = TimeSpan.FromMinutes(5);
	while (now < runTime && Volatile.Read(ref stopRequested) == 0) {
		TimeSpan remaining = runTime - now;
		Console.Write("\rRun In: ");
		Console.ForegroundColor = remaining < redTimeSpan ? ConsoleColor.Red : ConsoleColor.Green;
		Console.Write($"{remaining.Days} Days {remaining:hh\\:mm\\:ss}");
		Console.ResetColor();
		Console.Write($" at {runTime:HH:mm:ss} on {runTime:MMM d}  (Control-C to exit)");
		Thread.Sleep((int)Math.Min(remaining.TotalMilliseconds, 500));
		now = DateTime.Now;
	}
	Console.WriteLine();

	return Volatile.Read(ref stopRequested) == 0;
}


// ================================
// ======== Required Steps ========
// ================================
// The step number is the order of execution. Numbers missing from this section are in the browser-mimicking section.

async Task Step1_GetEventsList(Context context) {
	// ---- Step 1: the events list. ----
	// GET /api/v2/public/by-url/{slug}/events   (docs/VOW_SNL_FLOW.md section 4)
	// - uuid + journey_id identify the event in every later call
	// - status is "open" | "coming_soon" | "closed" (the front end shows a Register button only for "open")
	// - capacity - attending_count = seats left
	// The browser also sends X-Socket-ID here, but it has no socket yet on the first call ("undefined" was sent and accepted).
	// Read-only, so it is retried on a 5xx or network failure.
	string json = await SendWithRetryAsync("1. get events list",
		() => ApiRequest(HttpMethod.Get, $"{Vow.ApiBase}/api/v2/public/by-url/{context.Series}/events", Vow.ProOrigin, accept: "application/json, text/plain, */*"));
	EventsResponse response = JsonSerializer.Deserialize<EventsResponse>(json, JsonOptions)
		?? throw new InvalidOperationException("Events response was empty.");
	context.Events = response.Events
		.OrderBy(e => DateTimeOffset.Parse(e.StartsAt))
		.ToList();
}

async Task Step4_LoadJourney(Context context) {
	// ---- Step 4: load the journey (what the registration page does on load). ----
	// GET /api/v2/events/{uuid}/journeys/{journey_id}/load-for-visitor   (docs/VOW_SNL_FLOW.md 5.2)
	// - Sets the Laravel cookies (XSRF-TOKEN, vow_session, ...) that later calls send back; the cookie jar keeps them.
	// - Gives the ids we need: landing step, RSVP step, the "Continue" action, and max_plus_ones (guest limit).
	// Step ids change every week (they belong to the journey), so they are read here and never hard-coded.
	VowEvent selected = context.SelectedEvent!;
	string json = await SendWithRetryAsync("4. load journey",
		() => ApiRequest(HttpMethod.Get, $"{Vow.ApiBase}/api/v2/events/{selected.Uuid}/journeys/{selected.JourneyId}/load-for-visitor", Vow.GoOrigin));
	context.Journey = JsonSerializer.Deserialize<JourneyResponse>(json, JsonOptions)
		?? throw new InvalidOperationException("Journey response was empty.");

	Journey journey = context.Journey.Journey;
	JourneyStep? landing = journey.Steps.FirstOrDefault(s => s.IsRoot);
	JourneyStep? rsvp = journey.Steps.FirstOrDefault(s => s.Type == "rsvp");
	Console.WriteLine($"   capacity {journey.Capacity}, signed up {journey.SignupCount}, over capacity: {context.Journey.IsOverCapacity}");
	Console.WriteLine($"   landing step {landing?.Id}, rsvp step {rsvp?.Id} (max plus-ones {rsvp?.Options?.MaxPlusOnes}), closed step {journey.CapacityStepId}");
	if (context.Journey.IsOverCapacity)
		WriteWarning("The server reports the event is over capacity; the RSVP will probably be refused.");
}

async Task Step8_SubmitRsvp(Context context) {
	// ---- Step 8: submit the registration. ----
	// PUT /api/v2/events/{uuid}/attendees/rsvp   (docs/VOW_SNL_FLOW.md 5.4)
	// - This call is NEVER retried or hedged: the endpoint is rate limited (x-ratelimit-limit: 10 per window, window and key unknown),
	//   every duplicate counts against it, and a retry of a request that may have succeeded could double-register.
	// - 422 {"error":"This event is full.","capacity_full":true,"capacity_step_id":N} means sold out (seen in the capture).
	// - 429 would mean the rate limit was hit (not seen). Do not retry immediately; wait for Retry-After.
	// - Success (2xx) was never captured, so the body is printed as-is.
	VowEvent selected = context.SelectedEvent!;
	var request = ApiRequest(HttpMethod.Put, $"{Vow.ApiBase}/api/v2/events/{selected.Uuid}/attendees/rsvp", Vow.GoOrigin,
		json: context.GetRsvpJson(), useSocketId: true);
	Reply reply = await SendAsync("8. submit RSVP", request);

	if (reply.Ok) {
		Console.ForegroundColor = ConsoleColor.Green;
		Console.WriteLine("   registered (2xx). Response shape is unconfirmed; body follows:");
		Console.ResetColor();
		Console.WriteLine($"   {Excerpt(reply.Body, 1000)}");
		context.RsvpAccepted = true;
		return;
	}

	if ((int)reply.Status == 429)
		throw new HttpRequestException($"8. submit RSVP was rate limited (429). Retry-After: {reply.RetryAfter ?? "(not given)"}. Not retrying.", null, reply.Status);

	if ((int)reply.Status == 422 && reply.Body.Contains("capacity_full", StringComparison.Ordinal))
		throw new InvalidOperationException($"The event is full: {Excerpt(reply.Body, 300)}");

	throw new HttpRequestException($"8. submit RSVP returned {(int)reply.Status} {reply.Status}: {Excerpt(reply.Body, 300)}", null, reply.Status);
}


// =======================================
// ==  Browser-mimicking steps (not proven required)  ==
// =======================================
// Calls the browser makes that no captured call is known to depend on. That does NOT mean they are unnecessary: on 2026-09-24 the RSVP gave
// the same "event is full" answer with or without them, but only a full event was available to test against.
// Sent only with --analytics; a failure is logged and ignored.

async Task Step2_OpenRegistrationPage(Context context) {
	// ---- Step 2: open the registration page (the register_url the Register button links to). ----
	// GET https://go.vow.app/event/{uuid}/journeys/{journey_id}  -> the Nuxt single-page-app shell. Only load balancer cookies are set.
	VowEvent selected = context.SelectedEvent!;
	await RunOptionalAsync("2. open registration page", async () => {
		var request = new HttpRequestMessage(HttpMethod.Get, $"{Vow.GoOrigin}/event/{selected.Uuid}/journeys/{selected.JourneyId}");
		AddBrowserHeaders(request);
		request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/apng,*/*;q=0.8");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
		request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
		Require(await SendAsync("2. open registration page", request));
	});
}

async Task Step3_CheckAuthUser(Context context) {
	// ---- Step 3: ask who is logged in. ----
	// GET /api/auth/user -> 401 {"message":"Unauthenticated."} for an anonymous visitor. That is the expected answer here.
	await RunOptionalAsync("3. check auth user", async () => {
		await SendAsync("3. check auth user (401 expected)", ApiRequest(HttpMethod.Get, $"{Vow.ApiBase}/api/auth/user", Vow.GoOrigin));
	});
}

async Task Step5_ConnectPusher(Context context) {
	// ---- Step 5: open the Pusher WebSocket and read the socket id. ----
	// docs/VOW_SNL_FLOW.md 5.6. The first message from the server is
	//   {"event":"pusher:connection_established","data":"{\"socket_id\":\"1677674.4468990\",\"activity_timeout\":120}"}
	// The socket id is sent as X-Socket-ID on the log-interaction and RSVP calls (Laravel uses it to skip the sender when it broadcasts).
	// No channel is subscribed to in the capture, so the socket only needs to stay open; it is disposed when the script ends.
	await RunOptionalAsync("5. connect pusher", async () => {
		var socket = new ClientWebSocket();
		socket.Options.SetRequestHeader("Origin", Vow.GoOrigin);
		socket.Options.SetRequestHeader("User-Agent", Vow.UserAgent);
		context.Pusher = socket;

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		await socket.ConnectAsync(new Uri(Vow.PusherUrl), timeout.Token);
		var buffer = new byte[4096];
		WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, timeout.Token);
		string message = Encoding.UTF8.GetString(buffer, 0, result.Count);

		using JsonDocument outer = JsonDocument.Parse(message);
		string? eventName = outer.RootElement.GetProperty("event").GetString();
		if (eventName != "pusher:connection_established")
			throw new InvalidOperationException($"Unexpected first Pusher message: {Excerpt(message, 200)}");
		using JsonDocument inner = JsonDocument.Parse(outer.RootElement.GetProperty("data").GetString()!);
		context.SocketId = inner.RootElement.GetProperty("socket_id").GetString();
		Console.WriteLine($"5. connect pusher: connected, socket id {context.SocketId}");
	});
}

async Task Step6_LogLandingView(Context context) {
	// ---- Step 6: report the landing page as the visitor's current step. ----
	JourneyStep? landing = context.Journey?.Journey.Steps.FirstOrDefault(s => s.IsRoot);
	if (landing == null) return;
	await LogInteractionAsync("6. log landing view", landing.Id, actionId: null);
}

async Task Step7_LogContinueClick(Context context) {
	// ---- Step 7: report the click on "Continue" (landing -> RSVP form). ----
	// The action id is the journey action that goes from the landing step to the RSVP step (function "to", label "Continue").
	Journey? journey = context.Journey?.Journey;
	JourneyStep? landing = journey?.Steps.FirstOrDefault(s => s.IsRoot);
	JourneyStep? rsvp = journey?.Steps.FirstOrDefault(s => s.Type == "rsvp");
	JourneyAction? cont = journey?.Actions.FirstOrDefault(a => a.From == landing?.Id && a.To == rsvp?.Id);
	if (rsvp == null) return;
	await LogInteractionAsync("7. log continue click", rsvp.Id, cont?.Id.ToString());
}

async Task Step9_LogRsvpResult(Context context) {
	// ---- Step 9: report the step the visitor ended on. ----
	// INFERRED (no success was captured): on success the page shows the step reached by the RSVP step's "rsvp_yes" action;
	// after a full-event 422 the browser logged the closed step (journey.capacity_step_id) with a null action.
	Journey? journey = context.Journey?.Journey;
	JourneyStep? rsvp = journey?.Steps.FirstOrDefault(s => s.Type == "rsvp");
	JourneyAction? yes = journey?.Actions.FirstOrDefault(a => a.From == rsvp?.Id && a.Function == "rsvp_yes");
	int? endStep = context.RsvpAccepted ? yes?.To : journey?.CapacityStepId;
	if (endStep == null) return;
	await LogInteractionAsync("9. log rsvp result", endStep.Value, actionId: null);
}


// =======================================
// ========  Helpers  ====================
// =======================================

DateTime GetNextThursday10Am(int msDelay) {
	DateTime now = DateTime.Now;
	int daysFromNow = ((7 + (int)DayOfWeek.Thursday - (int)now.DayOfWeek) % 7);
	DateTime targetDate = now.Date.AddDays(daysFromNow);
	int milliseconds = msDelay % 1000;
	int seconds = (msDelay - milliseconds) / 1000;
	return targetDate.AddHours(10).AddSeconds(seconds).AddMilliseconds(milliseconds);
}

void WriteWarning(string message) {
	Console.ForegroundColor = ConsoleColor.Red;
	Console.Write("   WARNING: ");
	Console.ResetColor();
	Console.WriteLine(message);
}

string Excerpt(string text, int max) => text.Length > max ? text[..max] + "..." : text;

// Headers a Chrome browser adds to every request.
void AddBrowserHeaders(HttpRequestMessage request) {
	request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
	request.Headers.TryAddWithoutValidation("sec-ch-ua", Vow.SecChUa);
	request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
	request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
	request.Headers.TryAddWithoutValidation("User-Agent", Vow.UserAgent);
}

// A cross-origin XHR/fetch to api.vow.app, as the vow.app front ends send it.
// origin is the front end that makes the call: https://go.vow.app (registration) or https://pro.vow.app (listing).
HttpRequestMessage ApiRequest(HttpMethod method, string url, string origin, string? json = null, string accept = "application/json", bool useSocketId = false) {
	var request = new HttpRequestMessage(method, url);
	AddBrowserHeaders(request);
	request.Headers.Referrer = new Uri(origin + "/");
	request.Headers.TryAddWithoutValidation("Origin", origin);
	request.Headers.TryAddWithoutValidation("Accept", accept);
	request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
	request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
	request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
	if (useSocketId && context.SocketId != null)
		request.Headers.TryAddWithoutValidation("X-Socket-ID", context.SocketId);
	if (json != null)
		request.Content = new StringContent(json, Encoding.UTF8, "application/json");
	return request;
}

// Logs the request (timestamp, id, step, method, url, headers, cookies, body) as one entry.
// The caller creates the id so it can also log the response under the same id.
async Task LogRequestAsync(Guid requestId, string label, HttpRequestMessage request) {
	string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
	var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
		.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}");
	string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();

	var entry = new StringBuilder();
	entry.AppendLine($"{timestamp} REQUEST {requestId} [{label}]");
	entry.AppendLine($"{request.Method} {request.RequestUri}");
	foreach (string header in headers)
		entry.AppendLine(header);
	// The handler adds the Cookie header after this point, so read what it will send from the jar.
	string cookies = context.CookieJar.GetCookieHeader(request.RequestUri!);
	entry.AppendLine($"Cookie: {(cookies == "" ? "(none)" : cookies)}");
	entry.AppendLine();
	entry.Append(body);
	lock (logItems)
		LogLn(entry.ToString());
}

// Logs the response (timestamp, id, elapsed, status, headers, body) as one entry, under the id its request was logged with.
void LogResponse(Guid requestId, TimeSpan elapsed, HttpResponseMessage response, string body) {
	string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
	var headers = response.Headers.Concat(response.Content.Headers)
		.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}");

	var entry = new StringBuilder();
	entry.AppendLine($"{timestamp} RESPONSE {requestId} after {elapsed.TotalMilliseconds:N0} ms");
	entry.AppendLine($"{(int)response.StatusCode} {response.StatusCode}");
	foreach (string header in headers)
		entry.AppendLine(header);
	entry.AppendLine();
	entry.Append(body);
	lock (logItems)
		LogLn(entry.ToString());
}

// Logs a request that got no response (timeout or network error), under the id its request was logged with.
void LogNoResponse(Guid requestId, TimeSpan elapsed, Exception ex) {
	string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
	string reason = ex switch {
		TaskCanceledException { InnerException: TimeoutException } => "timed out",
		OperationCanceledException => "canceled",
		_ => "failed",
	};

	var entry = new StringBuilder();
	entry.AppendLine($"{timestamp} NO RESPONSE {requestId} after {elapsed.TotalMilliseconds:N0} ms");
	entry.AppendLine(reason);
	for (Exception? e = ex; e != null; e = e.InnerException)
		entry.AppendLine($"{e.GetType().Name}: {e.Message}");
	lock (logItems)
		LogLn(entry.ToString());
}

// Sends the request, prints one line, and returns the reply. Never throws on a status code (the caller decides).
async Task<Reply> SendAsync(string label, HttpRequestMessage request) {
	Guid requestId = Guid.NewGuid();
	await LogRequestAsync(requestId, label, request);
	var stopwatch = System.Diagnostics.Stopwatch.StartNew();
	HttpResponseMessage response;
	string body;
	try {
		response = await http.SendAsync(request);
		body = await response.Content.ReadAsStringAsync();
	}
	catch (Exception ex) {
		LogNoResponse(requestId, stopwatch.Elapsed, ex);
		throw;
	}
	using var _ = response;
	LogResponse(requestId, stopwatch.Elapsed, response, body);
	string? rateRemaining = response.Headers.TryGetValues("x-ratelimit-remaining", out var rem) ? rem.First() : null;
	string? rateLimit = response.Headers.TryGetValues("x-ratelimit-limit", out var lim) ? lim.First() : null;
	string? retryAfter = response.Headers.TryGetValues("Retry-After", out var retry) ? retry.First() : null;
	string rate = rateLimit != null ? $"  [rate limit {rateRemaining ?? "?"}/{rateLimit} left]" : "";
	Console.WriteLine($"{label}: {request.Method} {request.RequestUri!.AbsolutePath} -> {(int)response.StatusCode}{rate}");
	return new Reply(response.StatusCode, body, rateRemaining, retryAfter);
}

// Any non-2xx throws (with the status attached so retries can tell a 5xx from a 4xx).
Reply Require(Reply reply) {
	if (reply.Ok)
		return reply;
	string hint = (int)reply.Status >= 500 && reply.RetryAfter != null ? $" (server asks to retry after {reply.RetryAfter} seconds)" : "";
	throw new HttpRequestException($"returned {(int)reply.Status} {reply.Status}{hint}: {Excerpt(reply.Body, 300)}", null, reply.Status);
}

// For READ-ONLY steps only: retries after a 5xx or a network error, a few times, one at a time (no duplicates in flight).
// Never use this for the RSVP: see Step8_SubmitRsvp and the rate-limit notes in docs/VOW_SNL_FLOW.md 5.4.
async Task<string> SendWithRetryAsync(string label, Func<HttpRequestMessage> makeRequest, int maxAttempts = 3, int delayMs = 500) {
	for (int attempt = 1; ; attempt++) {
		try {
			return Require(await SendAsync(label, makeRequest())).Body;
		}
		catch (Exception ex) when (attempt < maxAttempts && IsRetryable(ex)) {
			Console.WriteLine($"   attempt {attempt} of {maxAttempts} failed ({ex.Message}); trying again in {delayMs} ms");
			await Task.Delay(delayMs);
		}
	}
}

bool IsRetryable(Exception ex) => ex is HttpRequestException { StatusCode: null } or TaskCanceledException
	|| ex is HttpRequestException { StatusCode: { } code } && (int)code >= 500;

async Task RunOptionalAsync(string label, Func<Task> action) {
	try {
		await action();
	}
	catch (Exception ex) {
		Console.Error.WriteLine($"{label}: browser-mimicking step failed and was skipped: {Excerpt(ex.Message, 300)}");
	}
}

async Task LogInteractionAsync(string label, int stepId, string? actionId) {
	// POST /api/v2/events/{uuid}/journeys/{journey_id}/log-interaction  {"step_id":N,"action_id":"N"|null,"session":<ms epoch>}
	// The response is not JSON (HTML content type) and is ignored. `session` is a client-generated millisecond epoch, constant for the visit.
	VowEvent selected = context.SelectedEvent!;
	string body = JsonSerializer.Serialize(new LogInteractionRequest { StepId = stepId, ActionId = actionId, Session = context.SessionMs }, new JsonSerializerOptions());
	await RunOptionalAsync(label, async () => {
		Require(await SendAsync(label, ApiRequest(HttpMethod.Post,
			$"{Vow.ApiBase}/api/v2/events/{selected.Uuid}/journeys/{selected.JourneyId}/log-interaction", Vow.GoOrigin,
			json: body, useSocketId: true)));
	});
}


// ================================
// ======== Known constants =======
// ================================
// Values observed in the captures (saz/snl_sep_24). They are fixed by the vow.app deployment or by our browser impersonation.
// If vow.app is redeployed, some of these can change; see the comments.
public static class Vow {

	// ---- Site ----
	/// <summary>The API host, used by both front ends (docs/VOW_SNL_FLOW.md section 2).</summary>
	public const string ApiBase = "https://api.vow.app";
	/// <summary>The registration single-page app. Sent as Origin/Referer on registration calls.</summary>
	public const string GoOrigin = "https://go.vow.app";
	/// <summary>The listing single-page app (iframed by snlstandby.nbcuni.com). Sent as Origin/Referer on the events-list call.</summary>
	public const string ProOrigin = "https://pro.vow.app";
	/// <summary>The "by-url" name NBC's SNL Standby page uses in /api/v2/public/by-url/{slug}/events.</summary>
	public const string SnlSlug = "nbc";

	// ---- Pusher ----
	/// <summary>Public Pusher app key (it is in the site's JavaScript) and cluster. May change if vow.app changes Pusher accounts.</summary>
	public const string PusherKey = "a7fadfa6cfe13872810f";
	public const string PusherCluster = "us2";
	/// <summary>WebSocket URL; protocol 7, pusher-js 8.4.0 as go.vow.app sent it.</summary>
	public static string PusherUrl => $"wss://ws-{PusherCluster}.pusher.com/app/{PusherKey}?protocol=7&client=js&version=8.4.0&flash=false";

	// ---- Rate limit (docs/VOW_SNL_FLOW.md 5.4) ----
	/// <summary>x-ratelimit-limit seen on the RSVP response: requests allowed per window. The window length is unknown. The counter was shared by separate runs
	/// with separate cookie jars (10 -> 9 -> 8 -> 7 ...), so it is not per cookie/session; most likely per client IP. A Cloudflare 403 did not count.
	/// log-interaction has its own, much larger limit (2000 seen).</summary>
	public const int RsvpRateLimit = 10;

	// ---- Browser impersonation (the captured browser was Chrome 153 on Windows) ----
	// A browser User-Agent is REQUIRED: Cloudflare in front of api.vow.app answers a script's default User-Agent (e.g. Python's)
	// with 403 "Error 1010: Access denied", on every endpoint including the events list. Tested 2026-09-24. HttpClient sends no User-Agent
	// unless we set one, so never drop it from AddBrowserHeaders.
	public const string ChromeVersion = "153";
	public static string UserAgent => $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromeVersion}.0.0.0 Safari/537.36";
	public static string SecChUa => $"\"Google Chrome\";v=\"{ChromeVersion}\", \"Not_A Brand\";v=\"8\", \"Chromium\";v=\"{ChromeVersion}\"";
}


// ==================================
// ======== Unknown variables =======
// ==================================
// Everything here is discovered or chosen at run time. Nothing in this class is safe to hard-code:
// event uuids, journey ids, step ids and action ids change every week, and cookies and the socket id are per visit.
public sealed class Context {
	// HTTP objects
	public HttpClient Http { get; set; } = null!;
	public CookieContainer CookieJar { get; set; } = new();

	// ------- config / options to select the desired event ------
	public string Series { get; set; } = "";	// by-url slug
	public string Show { get; set; } = "";

	// User Properties (the RSVP form has only these fields; there is no phone number)
	public string FirstName { get; set; } = "Test";
	public string LastName { get; set; } = "Dummy";
	public string Email { get; set; } = "test.dummy@example.com";
	public int GroupSize { get; set; } = 1;

	// ---- Discovered by step 1 ----
	public List<VowEvent> Events { get; set; } = [];
	public VowEvent? SelectedEvent { get; set; }

	// ---- Discovered by step 4 ----
	public JourneyResponse? Journey { get; set; }

	// ---- Discovered by step 5 (browser-mimicking, --analytics only) ----
	public ClientWebSocket? Pusher { get; set; }
	/// <summary>Pusher socket id, e.g. "1677674.4468990". Null when step 5 was skipped or failed, in which case no X-Socket-ID header is sent.</summary>
	public string? SocketId { get; set; }

	// ---- Per-visit ----
	/// <summary>The "session" value the page puts in log-interaction bodies: a client-generated millisecond epoch, constant for the visit.</summary>
	public long SessionMs { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	// ---- Result of step 8 ----
	public bool RsvpAccepted { get; set; }

	public void InitializeFrom(UserConfig config) {
		Console.WriteLine($"Using config:\r\n\tseries: {config.Series},\r\n\tshow: \"{config.Show}\",\r\n\tattendee: {config.FirstName} {config.LastName},\r\n\temail: {config.Email},\r\n\tgroup size: {config.GroupSize}\r\n");
		Series = config.Series;
		Show = config.Show;
		FirstName = config.FirstName;
		LastName = config.LastName;
		Email = config.Email;
		GroupSize = config.GroupSize;
	}

	// helper methods

	/// <summary>
	/// The RSVP form's "plus_ones" is the number of guests BEYOND the registrant. INFERRED: our GroupSize counts everyone,
	/// so plus_ones = GroupSize - 1, limited by the RSVP step's options.max_plus_ones (1 in the capture). Not confirmed by a success capture.
	/// </summary>
	public int GetPlusOnesToRequest() {
		int maxPlusOnes = Journey?.Journey.Steps.FirstOrDefault(s => s.Type == "rsvp")?.Options?.MaxPlusOnes ?? 0;
		int wanted = Math.Max(GroupSize - 1, 0);
		if (wanted > maxPlusOnes)
			Console.WriteLine($"   WARNING: group size {GroupSize} needs {wanted} plus-one(s) but this journey allows {maxPlusOnes}; reduced to fit.");
		return Math.Min(wanted, maxPlusOnes);
	}

	public string GetRsvpJson() {
		VowEvent selected = SelectedEvent ?? throw new InvalidOperationException("No event has been selected.");
		var body = new RsvpRequest {
			PlusOnes = GetPlusOnesToRequest(),
			JourneyId = selected.JourneyId,
			FirstName = FirstName,
			LastName = LastName,
			Email = Email,
		};
		// Null "me" and "group_id" are sent explicitly, as the browser does, so no ignore-null option here.
		return JsonSerializer.Serialize(body, new JsonSerializerOptions());
	}
}


// ==================================
// ======== Config / DTOs ===========
// ==================================
public sealed class UserConfig {
	public string Series { get; set; } = "";	// by-url slug
	public string Show { get; set; } = "";		// event selector (part of the event name)
	public string FirstName { get; set; } = "";
	public string LastName { get; set; } = "";
	public string Email { get; set; } = "";
	public int GroupSize = 1;
	public string? RunAt { get; set; }
}

public sealed class RunConfig {
	public bool Analytics { get; set; }
	public bool Submit { get; set; }
	public bool ListOnly { get; set; }
}

public sealed record Reply(HttpStatusCode Status, string Body, string? RateRemaining, string? RetryAfter) {
	public bool Ok => (int)Status is >= 200 and < 300;
}

// ---- Step 1 response ----
public sealed class EventsResponse {
	[JsonPropertyName("events")]
	public List<VowEvent> Events { get; set; } = [];
}

public sealed class VowEvent {
	[JsonPropertyName("uuid")]
	public string Uuid { get; set; } = "";

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("starts_at")]
	public string StartsAt { get; set; } = "";

	[JsonPropertyName("journey_id")]
	public int JourneyId { get; set; }

	[JsonPropertyName("register_url")]
	public string? RegisterUrl { get; set; }

	[JsonPropertyName("capacity")]
	public int Capacity { get; set; }

	[JsonPropertyName("attending_count")]
	public int AttendingCount { get; set; }

	/// <summary>"open" | "coming_soon" | "closed". Only "closed" was seen in data; "open" and "coming_soon" come from the front-end code.</summary>
	[JsonPropertyName("status")]
	public string Status { get; set; } = "";

	[JsonIgnore] public bool IsOpen => Status == "open";
	[JsonIgnore] public int SlotsAvailable => Math.Max(Capacity - AttendingCount, 0);
	[JsonIgnore] public bool HasPassed => DateTimeOffset.Parse(StartsAt) < DateTimeOffset.UtcNow;

	// Other properties not used: timezone, location{...}, opens_at, closes_at, theme{...}
}

// ---- Step 4 response ----
public sealed class JourneyResponse {
	[JsonPropertyName("journey")]
	public Journey Journey { get; set; } = new();

	[JsonPropertyName("is_over_capacity")]
	public bool IsOverCapacity { get; set; }

	// Other top-level properties not used: attendees, event, me
}

public sealed class Journey {
	[JsonPropertyName("id")]
	public int Id { get; set; }

	[JsonPropertyName("capacity")]
	public int Capacity { get; set; }

	[JsonPropertyName("signup_count")]
	public int SignupCount { get; set; }

	/// <summary>The id of the "Closed" step the page shows when the RSVP is refused because the event is full.</summary>
	[JsonPropertyName("capacity_step_id")]
	public int CapacityStepId { get; set; }

	[JsonPropertyName("steps")]
	public List<JourneyStep> Steps { get; set; } = [];

	[JsonPropertyName("actions")]
	public List<JourneyAction> Actions { get; set; } = [];
}

public sealed class JourneyStep {
	[JsonPropertyName("id")]
	public int Id { get; set; }

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	/// <summary>"page" (landing, confirmation, closed) or "rsvp" (the form).</summary>
	[JsonPropertyName("type")]
	public string Type { get; set; } = "";

	[JsonPropertyName("is_root")]
	public bool IsRoot { get; set; }

	[JsonPropertyName("options")]
	public StepOptions? Options { get; set; }
}

public sealed class StepOptions {
	/// <summary>Guests allowed beyond the registrant (1 for the SNL RSVP step).</summary>
	[JsonPropertyName("max_plus_ones")]
	public int MaxPlusOnes { get; set; }
}

public sealed class JourneyAction {
	[JsonPropertyName("id")]
	public int Id { get; set; }

	[JsonPropertyName("from")]
	public int From { get; set; }

	[JsonPropertyName("to")]
	public int To { get; set; }

	/// <summary>"to" (plain navigation), "rsvp_yes" (the submit button) or "condition".</summary>
	[JsonPropertyName("function")]
	public string Function { get; set; } = "";
}

// ---- Step 8 request ----
public sealed class RsvpRequest {
	[JsonPropertyName("rsvp")]
	public bool Rsvp { get; set; } = true;
	[JsonPropertyName("plus_ones")]
	public int PlusOnes { get; set; }
	[JsonPropertyName("me")]
	public object? Me { get; set; }
	[JsonPropertyName("journey")]
	public int JourneyId { get; set; }
	[JsonPropertyName("group_id")]
	public object? GroupId { get; set; }
	[JsonPropertyName("first_name")]
	public string FirstName { get; set; } = "";
	[JsonPropertyName("last_name")]
	public string LastName { get; set; } = "";
	[JsonPropertyName("email")]
	public string Email { get; set; } = "";
}

// ---- log-interaction request ----
public sealed class LogInteractionRequest {
	[JsonPropertyName("step_id")]
	public int StepId { get; set; }
	[JsonPropertyName("action_id")]
	public string? ActionId { get; set; }
	[JsonPropertyName("session")]
	public long Session { get; set; }
}
