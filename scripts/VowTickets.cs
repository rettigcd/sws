#:property PublishAot=false
// Signs up for SNL Standby tickets through the vow.app registration site (which replaced the Qudini booking widget).
// It sends the same API calls a browser sends. --mode picks how much of the browser's flow is copied, because which steps the
// server needs is NOT known. Which steps are needed was tested on 2026-09-24 against a full event; see "What was tested" below.
// Everything here is based on the captures saz/snl_sep_24/snl_sep_24_part_1.saz and snl_sep_24_part_2.saz,
// documented in docs/VOW_SNL_FLOW.md (read it first; section numbers below refer to it).
//
// Run:      dotnet run scripts/VowTickets.cs -- --config FILE [--mode 1|2|3|min|partial|full] [--submit] [--list] [--nowait]
//                                               [--title TEXT] [--event UUID] [--group-size N] [--series SLUG] [--help]
// --config: REQUIRED. The JSON file with the attendee and show (see UserConfig; example: credentials/dean.json). The .json suffix is
//           optional, and a bare name is also looked for in the credentials/ folder (git-ignored: these files hold personal data).
//           --title, --event, --group-size and --series override the file, in any order.
// Default:  a dry run in mode 2. The steps before the RSVP are sent, then the RSVP request is printed but NOT sent. Add --submit to send it.
// --list:   print every event (uuid, start, status, seats, name) after step 1 and stop. Skips the wait for the run time.
// --nowait: skip the wait for the run time (Thursday 09:59:59 for SNL configs) and start right away.
// Retries: 5xx answers and network errors are retried one try at a time. The first events list and the journey load keep trying every second for
//           up to 12 s; the RSVP gets up to 5 tries, 2 s apart, and none when the rate limit counter is low (see RsvpMaxAttempts and the constants at the top).
// --replay-server PORT|URL: test against the local replay server (replay-server/ReplayServer.cs) instead of the real vow.app. PORT is shorthand for
//           http://localhost:PORT. The API, the registration page and the listing page are all that one origin. The Pusher connection (mode 3) is
//           skipped (the replay server has none) and the wait for the run time is skipped (the replay server decides when the show opens; see its
//           /__replay/open-in/N), except with --open-show-in, which makes the run wait for the open time it sets. Nothing is sent to the real site.
// --open-show-in SECONDS: only with --replay-server. Before anything else, asks the replay server to put the show list back to coming_soon and open
//           it SECONDS from now (its test endpoint /__replay/open-in/N; SECONDS may be fractional, 0 = open at once, at most 86400). The server also
//           gives both shows new random event and journey ids, which this script then reads from the list as usual. The run time becomes that open
//           time less 1 s (like Thursday 09:59:59 for a 10:00 open) and the run WAITS for it with the usual "Run In:" countdown, unless --nowait.
// Choosing: done at the run time from the events list; no uuid is stored anywhere. --event UUID picks by uuid; --title TEXT, or "Show" in the config
//           file, picks the first upcoming event whose name contains the text (any case). If nothing matches, the first event that is open, has seats
//           and has not passed is used instead.
// Early check: while waiting for the run time, the same choice is tested against the events list at the start and then every minute (green: found;
//           red: no event matches, with the names the list has; yellow: list empty or unreadable). It prints only when the answer changes, so there
//           is time to fix the config's Show before the opening. It never stops the run.
// --mode:   what is sent once the event is open (all modes first poll the events list until it is open):
//           1 (or min, minimal) = just the RSVP. Fastest; plus_ones is not checked against the journey's limit (assumed 1, as seen 2026-09-24 and 2026-10-01).
//           2 (or partial) = load the journey (load-for-visitor), then the RSVP. (default)
//           3 (or full) = copy the browser: page view (not waited for), auth check, load the journey, Pusher WebSocket (for X-Socket-ID),
//               log-interaction for the landing page and the Continue click (sent WITHOUT waiting for the answers, like the page: it does
//               not await them either), the RSVP, then log-interaction for the result (also not waited for; the log is completed at the end).
//           Whether modes 1 and 2 are enough is untested against an event that has room (see below).
//           Mode 3 sends every call after the event opens, in the browser's order. The auth check and the Pusher connection do not
//           depend on the event, so they COULD be sent during the polling to save time at the opening; see steps 3 and 5.
// --series: the vow.app "by-url" slug that lists the events. SNL is "nbc". (Qudini's series id has no equivalent.)
// Note:     --submit registers with the data in the config and takes a real spot. Use it only when that is what you want.
//
// What was tested (2026-09-24, both events full, RSVP sent with placeholder data; docs/VOW_SNL_FLOW.md 7.6):
//   The RSVP got the same answer, 422 "This event is full", with: mode 2; mode 3; no cookies at all; a made-up X-Socket-ID;
//   and no Origin/Referer. So none of those is checked BEFORE the capacity check. This does not show they are unchecked for an event with room.
//   A browser User-Agent IS required: Cloudflare answers any request with a script's default User-Agent with 403 "Error 1010" (all api.vow.app endpoints).
//
// !!! The success response of the RSVP call was never captured (both shows were full). Step 8 treats any 2xx as success and prints the
// !!! body. Everything about the success path is inferred from the site's JavaScript. Verify against a capture of an open show.

// ==== TODO ====
// Confirm the RSVP success response shape, and then read the booking/confirmation number from it.
// Confirm whether X-Socket-ID / Pusher / log-interaction / cookies / Origin are required once an event has room (untested; all skipped below mode 3 or not enforced when full).
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

// The run starts this long BEFORE the show opens (Thursday 09:59:59 for a 10:00 open): the first request, and its connection setup, happen
// before the opening and the polling catches it. Used for the weekly run time and for --open-show-in.
const int StartBeforeOpenMs = 1000;

// ---- Retries after server errors (5xx) and network errors. Everything below is one at a time: nothing is ever sent twice at once. ----
// Read-only calls (the first events list, the journey): retry every RetryDelayMs for up to RetryBudgetSeconds counted from the first try,
// so a spell of server errors of up to about 10 s is survived.
const int RetryDelayMs = 1000;
const int RetryBudgetSeconds = 12;
// The RSVP is rate limited (x-ratelimit-limit 10 per window, shared) and a repeat of a request that may have got through could double-register,
// so it gets few tries, spaced further apart, and none once the limit's counter is low. Only 5xx answers and network errors are retried;
// 422 (full), 429 (rate limited) and the other 4xx never are. 5 tries 2 s apart survive about 8 s of errors, more when the errors are slow.
const int RsvpMaxAttempts = 5;
const int RsvpRetryDelayMs = 2000;
const int RsvpMinRateRemaining = 3;

// ---- Time limits for one request. HttpClient's own limit is 100 s, so without these a request that hangs would hold things up for that long. ----
// A request that hangs is given up on (the answer, if it ever comes, is not wanted any more) and the retry or the next poll goes on.
const int PollTimeoutMs = 5000;		// the events list: each poll (they overlap, so one that hangs just frees its slot), the first list and the show check (retried). It answers in about 0.1 s; 2.2 s was the worst seen at the 2026-10-01 go-live.
const int JourneyTimeoutMs = 10000;	// load-for-visitor: slow at the go-live (6.7 s seen 2026-10-01), so it is not cut off early.
const int JourneyBudgetSeconds = 30;	// retries of the journey load stop once this much time has passed since the first try (RetryBudgetSeconds is for the events list)
const int OptionalTimeoutMs = 10000;	// the browser-mimicking calls (registration page, auth check, log-interaction): the RSVP does not depend on them
// The RSVP has NO limit of its own on purpose (it keeps HttpClient's 100 s): it is the one call that must not be cut off, because the server may be
// about to accept it. What happens after a failed RSVP is described in Step8_SubmitRsvp.

// While waiting for the run time, the show name from the config (or --title / --event) is checked against the events list once at the start and then
// every this many seconds, so a wrong or renamed show is found with time to fix it, not at 09:59:59 when the selection is made.
const int ShowCheckEverySeconds = 60;

var context = new Context();
var runConfig = new RunConfig();
Func<VowEvent, bool> available = e => e.IsOpen && e.SlotsAvailable > 0 && !e.HasPassed;
Func<VowEvent, bool> selector = available;
DateTime runTime = DateTime.Now;
string selectorText = "";	// what the selector looks for, in words: the config's Show, or --title / --event
string lastShowCheck = "";	// the text of the last show check that was printed; a check prints only when its result changes
DateTime scriptStart = DateTime.Now;

const string Usage = "Usage: dotnet run scripts/VowTickets.cs -- --config FILE [--mode 1|2|3|min|partial|full] [--submit] [--list] [--nowait] [--title TEXT] [--event UUID] [--group-size N] [--series SLUG] [--replay-server PORT|URL] [--open-show-in SECONDS] [--help]   (--help describes each option)";

// --help: every option, one per line, in alphabetical order
string[] helpLines = {
	"VowTickets: registers for SNL Standby tickets on vow.app. Usage: dotnet run scripts/VowTickets.cs -- --config FILE [options]",
	"",
	"  --config FILE             REQUIRED. JSON file with the attendee and show (the .json suffix is optional; a bare name is also looked for in credentials/).",
	"  --event UUID              Use the event with this uuid. Overrides the config's Show.",
	"  --group-size N            Total people including you. Overrides the config.",
	"  --help                    Show this list and exit.",
	"  --list                    Print every event (uuid, start, status, seats, name) and stop. Skips the wait for the run time.",
	"  --mode MODE               What is sent once the event is open: min (or 1) = just the RSVP; partial (or 2) = load the journey, then the RSVP (the default); full (or 3) = copy the browser.",
	"  --nowait                  Do not wait for the run time (Thursday 09:59:59 for SNL configs); start right away.",
	"  --open-show-in SECONDS    Only with --replay-server: make the replay server's show open SECONDS from now (0 to 86400). The run waits for it.",
	"  --replay-server PORT|URL  Test against the local replay server instead of vow.app (PORT means http://localhost:PORT). Nothing is sent to the real site.",
	"  --series SLUG             The vow.app list name (SNL is nbc). Overrides the config.",
	"  --submit                  Really send the RSVP. Without it the run is a dry run: the RSVP is printed, not sent.",
	"  --title TEXT              Use the first upcoming event whose name contains TEXT (any case). Overrides the config's Show.",
};
string? configArg = null;
string? seriesOverride = null;
string? replayServer = null;
double? openShowIn = null;
int? groupSizeOverride = null;
Func<VowEvent, bool>? selectorOverride = null;
string? selectorOverrideText = null;	// what selectorOverride looks for, in words, for the early show check
for (int i = 0; i < args.Length; i++) {
	switch (args[i]) {

		case "--help": foreach (string line in helpLines) Console.WriteLine(line); return 0;

		// attendee + show file (required; applied after all arguments are read, so the overrides below win)
		case "--config" when i + 1 < args.Length: configArg = args[++i]; break;
		case "--series" when i + 1 < args.Length: seriesOverride = args[++i]; break;

		// event selection
		case "--event" when i + 1 < args.Length: {
			string eventUuid = args[++i];
			selectorOverride = e => string.Equals(e.Uuid, eventUuid, StringComparison.OrdinalIgnoreCase);
			selectorOverrideText = $"--event {eventUuid}";
			break;
		}
		case "--title" when i + 1 < args.Length: {
			string titleText = args[++i];
			selectorOverride = e => e.Name.Contains(titleText, StringComparison.OrdinalIgnoreCase) && !e.HasPassed;
			selectorOverrideText = $"--title \"{titleText}\"";
			break;
		}

		// config options
		case "--mode" when i + 1 < args.Length: {
			int mode = args[++i].ToLowerInvariant() switch {
				"1" or "min" or "minimal" => 1,
				"2" or "partial" => 2,
				"3" or "full" => 3,
				_ => 0,
			};
			if (mode == 0) {
				Console.Error.WriteLine("--mode must be 1|min|minimal, 2|partial or 3|full.");
				Console.Error.WriteLine(Usage);
				return 2;
			}
			runConfig.Mode = mode;
			break;
		}
		case "--submit": runConfig.Submit = true; break;
		case "--list": runConfig.ListOnly = true; break;
		case "--nowait": runConfig.Wait = false; break;
		case "--replay-server" when i + 1 < args.Length: replayServer = args[++i]; break;
		case "--open-show-in" when i + 1 < args.Length: {
			if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds) || seconds < 0 || seconds > 86400) {
				Console.Error.WriteLine($"--open-show-in must be a number of seconds from 0 to 86400 (got '{args[i]}').");
				return 2;
			}
			openShowIn = seconds;
			break;
		}
		// user form info
		case "--group-size" when i + 1 < args.Length: groupSizeOverride = int.Parse(args[++i]); break;

		default:
			Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
			Console.Error.WriteLine(Usage);
			return 2;
	}
}

// --open-show-in only makes sense against the replay server: it must never be sent to the real site.
if (openShowIn != null && replayServer == null) {
	Console.Error.WriteLine("--open-show-in only works together with --replay-server (it sets the open time on the replay server).");
	return 2;
}

// ---- --replay-server: point everything at the local replay server (before anything is sent) ----
if (replayServer != null) {
	string baseUrl = replayServer.All(char.IsDigit) ? $"http://localhost:{replayServer}" : replayServer.TrimEnd('/');
	if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var replayUri) || (replayUri.Scheme != "http" && replayUri.Scheme != "https")) {
		Console.Error.WriteLine($"--replay-server must be a port number or an http(s) URL (got '{replayServer}').");
		return 2;
	}
	Vow.UseReplayServer(baseUrl);
	if (openShowIn == null) runConfig.Wait = false;   // with --open-show-in the run waits for that open time (minus StartBeforeOpenMs), unless --nowait
	Console.WriteLine($"REPLAY SERVER: every call goes to {baseUrl}, not to vow.app. No Pusher. " +
		(openShowIn == null ? "No wait for the run time." : runConfig.Wait ? "The run waits for the open time set by --open-show-in." : "No wait (--nowait)."));
}

// ---- Load the config file, then apply the command-line overrides. ----
if (configArg == null) {
	Console.Error.WriteLine("Missing --config FILE (the JSON file with the attendee and show, e.g. credentials/dean.json).");
	Console.Error.WriteLine(Usage);
	return 2;
}
string? configPath = FindConfigFile(configArg);
if (configPath == null) {
	Console.Error.WriteLine($"Config file not found: {configArg} (also tried with .json, and in the credentials folder).");
	return 2;
}
UserConfig config;
try {
	config = JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(configPath), JsonOptions)
		?? throw new InvalidOperationException("the file is empty");
}
catch (Exception ex) {
	Console.Error.WriteLine($"Could not read {configPath}: {ex.Message}");
	return 2;
}
Console.WriteLine($"Config file: {Path.GetFullPath(configPath)}");
context.InitializeFrom(config);
selector = e => e.Name.Contains(context.Show, StringComparison.OrdinalIgnoreCase) && !e.HasPassed;
if (config.RunAt == ForSNL)
	runTime = GetNextThursday10Am(-StartBeforeOpenMs);	// 09:59:59: the first request (and its connection setup) happens before the opening; the polling catches it
if (seriesOverride != null) context.Series = seriesOverride;
if (groupSizeOverride != null) context.GroupSize = groupSizeOverride.Value;
if (selectorOverride != null) selector = selectorOverride;
selectorText = selectorOverrideText ?? $"the config's Show \"{context.Show}\"";

if (context.Series == "") {
	Console.Error.WriteLine("No series: set \"Series\" in the config file (SNL is \"nbc\") or pass --series SLUG.");
	Console.Error.WriteLine(Usage);
	return 2;
}

// Control-C during the wait (WaitUntilRunTime) or the polling (Step1b_PollUntilOpen) stops cleanly, so the log is still written.
// Outside those two, Control-C keeps its default behavior (the process ends at once) so it can always interrupt a stuck call.
var stop = new CancellationTokenSource();
bool ctrlCStopsCleanly = false;
Console.CancelKeyPress += (_, eventArgs) => {
	if (!ctrlCStopsCleanly) return;
	eventArgs.Cancel = true;
	stop.Cancel();
	Console.WriteLine("\nCancelled. Exiting.");
};

// ONE connection per host, using HTTP/2 (requests ask for it; see ApiRequest). HTTP/2 multiplexes, so the overlapping polls and
// the RSVP all share the connection the first request opened (at 09:59:59) and never pay for opening a new one.
// Tested 2026-10-01: api.vow.app (Cloudflare) answers HTTP/2; 4 concurrent polls on one connection took 88-161 ms each.
// .NET opens a single HTTP/2 connection per host by default (SocketsHttpHandler.EnableMultipleHttp2Connections = false);
// MaxConnectionsPerServer = 1 keeps it to one connection if the server ever falls back to HTTP/1.1 (overlapping polls then queue).
// UNTESTED for the RSVP: every RSVP so far (the 2026-09-24 script tests, the 2026-10-01 capture through Fiddler) was HTTP/1.1. HTTP/2 is what Chrome uses.
using var handler = new HttpClientHandler {
	CookieContainer = context.CookieJar,
	UseCookies = true,
	AutomaticDecompression = DecompressionMethods.All,
	// With --replay-server the replay server speaks plain HTTP/1.1, where overlapping requests need a connection each (the real site's HTTP/2
	// shares one connection between them), so allow several; one connection would queue them and hide the effect of not waiting.
	MaxConnectionsPerServer = Vow.IsReplay ? 8 : 1,
};
using var http = new HttpClient(handler);
context.Http = http;

var logItems = new List<string>();
void LogLn(string s){ logItems.Add(s); logItems.Add("\r\n"); }
LogLn($"{scriptStart:yyyy-MM-dd HH:mm:ss.fff} SCRIPT STARTED");
// The page view (step 2) is not awaited where it is sent; it is awaited at the end so its response still gets logged.
Task? pageView = null;
// The log-interaction reports (steps 6, 7 and 9) are not waited for either: the site's code sends them and carries on, and so does this
// script. They are awaited at the end so their responses still get logged.
var background = new List<Task>();

try {
	// ---- --open-show-in: set the replay server's open time first, so everything below sees the show as coming soon until then ----
	if (openShowIn != null)
		await OpenShowInAsync(openShowIn.Value);

	// ---- early check of the show name (again every ShowCheckEverySeconds during the wait) ----
	if (runConfig.Wait && !runConfig.ListOnly && runTime > DateTime.Now)
		await CheckShowNameAsync();

	// ---- wait ----
	// Inside the try so that stopping here still writes the log (in finally).
	if (runConfig.Wait && !WaitUntilRunTime())
		return 0;

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

	// Before it opens, the list omits journey_id (and capacity, register_url, ...), so poll until it appears.
	if (context.SelectedEvent.Status == "closed" && context.SelectedEvent.JourneyId == 0)
		throw new InvalidOperationException($"\"{context.SelectedEvent.Name}\" is closed. Nothing to wait for.");
	if (context.SelectedEvent.JourneyId == 0)
		await Step1b_PollUntilOpen(context);

	Console.ForegroundColor = ConsoleColor.Green;
	Console.Write($"   Selected Event: \"{context.SelectedEvent.Name}\" on {context.SelectedEvent.StartsAt}, status {context.SelectedEvent.Status}, {context.SelectedEvent.SlotsAvailable} seats ");
	Console.ResetColor();
	Console.WriteLine($"(uuid {context.SelectedEvent.Uuid} journey {context.SelectedEvent.JourneyId})");
	Console.WriteLine($"   mode {runConfig.Mode}: {runConfig.Mode switch { 1 => "just the RSVP", 2 => "load the journey, then the RSVP", _ => "copy the browser" }}");

	// 2-3. What the browser does when the registration page opens. (mode 3 only; not proven required)
	// The page view is HTML from go.vow.app that the API cannot see, so nothing waits for it (it runs alongside the calls below).
	if (runConfig.Mode == 3) {
		pageView = Step2_OpenRegistrationPage(context);
		await Step3_CheckAuthUser(context);		// possible early call (see the step)
	}

	// 4. Load the journey: sets the cookies and gives the step/action ids and the guest limit. (modes 2 and 3; not proven required)
	if (runConfig.Mode >= 2)
		await Step4_LoadJourney(context);

	// 5-7. Pusher connection and the "visitor clicked through the landing page" calls. (mode 3 only; not proven required)
	if (runConfig.Mode == 3) {
		await Step5_ConnectPusher(context);		// possible early call (see the step)
		// The page does not wait for these two reports (it does not await logInteraction), so they go out together, with no wait, and
		// the RSVP follows at once. Waiting for them cost two slow round trips before the RSVP (20.9 s against about 10 s with the replay
		// server under its default load).
		background.Add(Step6_LogLandingView(context));
		background.Add(Step7_LogContinueClick(context));
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

	// 9. Tell the server which step we ended on. (mode 3 only; not proven required)
	if (runConfig.Mode == 3)
		background.Add(Step9_LogRsvpResult(context));

	return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) {
	// Control-C while polling (the message was printed by the handler).
	return 0;
}
catch (Exception ex) {
	Console.Error.WriteLine($"FAILED: {ex.Message}");
	return 1;
}
finally {
	ctrlCStopsCleanly = false;
	// Give a still-running page view and the log-interaction reports a few seconds to finish so their responses are in the log.
	// They never throw (RunOptionalAsync).
	var stillRunning = background.Append(pageView).Where(t => t != null && !t.IsCompleted).Select(t => t!).ToList();
	if (stillRunning.Count > 0)
		await Task.WhenAny(Task.WhenAll(stillRunning), Task.Delay(TimeSpan.FromSeconds(10)));
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

	ctrlCStopsCleanly = true;
	var redTimeSpan = TimeSpan.FromMinutes(5);
	var nextShowCheck = now.AddSeconds(ShowCheckEverySeconds);
	while (now < runTime && !stop.IsCancellationRequested) {
		if (now >= nextShowCheck && !runConfig.ListOnly) {	// not awaited: the countdown goes on while the list is fetched
			nextShowCheck = now.AddSeconds(ShowCheckEverySeconds);
			_ = CheckShowNameAsync();
		}
		TimeSpan remaining = runTime - now;
		Console.Write("\rRun In: ");
		Console.ForegroundColor = remaining < redTimeSpan ? ConsoleColor.Red : ConsoleColor.Green;
		Console.Write($"{remaining.Days} Days {remaining:hh\\:mm\\:ss}");
		Console.ResetColor();
		Console.Write($" at {runTime:HH:mm:ss} on {runTime:MMM d}  (Control-C to exit)");
		stop.Token.WaitHandle.WaitOne((int)Math.Min(remaining.TotalMilliseconds, 500));
		now = DateTime.Now;
	}
	ctrlCStopsCleanly = false;
	Console.WriteLine();

	return !stop.IsCancellationRequested;
}

// Fetches the events list and says whether the chosen show (the same selector Step 1 will use) is in it. Prints only when the answer changes, so a
// check every minute stays quiet. A failure to check is reported but never stops the run: Step 1 asks again at the run time.
async Task CheckShowNameAsync() {
	ConsoleColor color;
	string message;
	try {
		List<VowEvent> events = ParseEvents(Require(await SendAsync("0. check the show name", EventsListRequest(), TimeSpan.FromMilliseconds(PollTimeoutMs))).Body);
		string names = string.Join(", ", events.Select(e => $"\"{e.Name}\" ({e.Status})"));
		List<VowEvent> matches = events.Where(selector).ToList();
		if (events.Count == 0) {
			color = ConsoleColor.Yellow;
			message = "Show check: the events list is empty (the shows are not posted yet). Will check again.";
		}
		else if (matches.Count == 0) {
			color = ConsoleColor.Red;
			message = $"Show check: NO EVENT MATCHES {selectorText}. The list has: {names}. Fix the config's Show (or --title / --event) before the opening.";
		}
		else {
			color = ConsoleColor.Green;
			message = $"Show check OK: \"{matches[0].Name}\" ({matches[0].Status}) will be used"
				+ (matches.Count > 1 ? $"; {matches.Count} events match, the first is taken. The list has: {names}" : ".");
		}
	}
	catch (Exception ex) {
		color = ConsoleColor.Yellow;
		message = $"Show check: could not read the events list ({Excerpt(ex.Message, 120)}). Will try again.";
	}
	if (message == lastShowCheck) return;
	lastShowCheck = message;
	Console.WriteLine();	// the countdown line is rewritten in place; start a line of its own
	Console.ForegroundColor = color;
	Console.WriteLine(message);
	Console.ResetColor();
	LogLn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");
}


// ================================
// ======== Required Steps ========
// ================================
// The step number is the order of execution. Numbers missing from this section are in the browser-mimicking section.

async Task Step1_GetEventsList(Context context) {
	// ---- Step 1: the events list. ----
	// GET /api/v2/public/by-url/{slug}/events   (docs/VOW_SNL_FLOW.md sections 4 and 11.2)
	// - uuid identifies the event in every later call; the journey id comes from register_url (see ParseEvents)
	// - status is "coming_soon" -> "open" -> "closed" (the front end shows a Register button only for "open")
	// The browser also sends X-Socket-ID here, but it has no socket yet on the first call ("undefined" was sent and accepted).
	// Read-only, so it is retried on a 5xx or network failure.
	string json = await SendWithRetryAsync("1. get events list", EventsListRequest, TimeSpan.FromMilliseconds(PollTimeoutMs));
	context.Events = ParseEvents(json);
}

HttpRequestMessage EventsListRequest() =>
	ApiRequest(HttpMethod.Get, $"{Vow.ApiBase}/api/v2/public/by-url/{context.Series}/events", Vow.ProOrigin, accept: "application/json, text/plain, */*");

List<VowEvent> ParseEvents(string json) {
	EventsResponse response = JsonSerializer.Deserialize<EventsResponse>(json, JsonOptions)
		?? throw new InvalidOperationException("Events response was empty.");
	var events = response.Events
		.OrderBy(e => DateTimeOffset.Parse(e.StartsAt))
		.ToList();
	// Seen 2026-10-01 (saz/vow/snl_oct_01): once open, the list has status and register_url but NO journey_id,
	// so take the journey id from the end of register_url (.../journeys/{journey_id}).
	foreach (var e in events)
		if (e.JourneyId == 0 && e.RegisterUrl != null && int.TryParse(e.RegisterUrl.TrimEnd('/').Split('/')[^1], out int journeyId))
			e.JourneyId = journeyId;
	return events;
}

async Task Step1b_PollUntilOpen(Context context, int intervalMs = 250, int maxInFlight = 4, int timeLimitMinutes = 15) {
	// ---- Step 1b: wait for the selected event to open. ----
	// Seen 2026-10-01 at 09:46: while "coming_soon", each event has only uuid, name, starts_at, theme and status.
	// journey_id is needed for every later call, so re-read the list until the selected event has one (taken from register_url; see ParseEvents).
	// In the 2026-10-01 log the show was already open at 10:00:00.5, and the Dress Rehearsal was full by about 10:00:41.
	//
	// Polls OVERLAP: a new one starts every intervalMs even if earlier ones have not answered, up to maxInFlight at once.
	// So one slow response (the browser saw 2.2 s on this call at 10:00:12 on 2026-10-01) cannot hide the opening; the first
	// response that shows the event open wins. The list endpoint showed no rate limit (2,619 polls at ~3/s on 2026-10-01).
	// Each poll is a single attempt (no retry): a failure is logged and the next poll is already on its way.
	string uuid = context.SelectedEvent!.Uuid;
	var deadline = DateTime.Now.AddMinutes(timeLimitMinutes);
	int polls = 0;
	WriteWarning($"\"{context.SelectedEvent.Name}\" is {context.SelectedEvent.Status} (no journey id yet). Polling every {intervalMs} ms "
		+ $"(up to {maxInFlight} at once) for up to {timeLimitMinutes} minutes (Control-C to exit).");

	// Each poll gives up after PollTimeoutMs (so a hung one frees its slot), and all polls still out are called off when this method ends (a winner, an error or Control-C).
	using var pollStop = new CancellationTokenSource();

	async Task<(int Number, List<VowEvent>? Events)> PollOnceAsync(int number) {
		try {
			string json = Require(await SendAsync($"1b. poll {number}", EventsListRequest(), TimeSpan.FromMilliseconds(PollTimeoutMs), pollStop.Token)).Body;
			return (number, ParseEvents(json));
		}
		catch (OperationCanceledException) when (pollStop.IsCancellationRequested) {
			return (number, null);	// called off because the polling is over
		}
		catch (Exception ex) {
			Console.WriteLine($"   poll {number} failed ({Excerpt(ex.Message, 200)}); the others continue");
			return (number, null);
		}
	}

	// Control-C stops the polling with an OperationCanceledException (caught in the main block, which writes the log).
	// Polls still in flight when this returns are called off (pollStop in the finally below); the call-off is logged as "canceled".
	ctrlCStopsCleanly = true;
	var cancelled = Task.Delay(Timeout.Infinite, stop.Token);
	var inFlight = new List<Task<(int Number, List<VowEvent>? Events)>>();
	var nextStart = DateTime.Now.AddMilliseconds(intervalMs);
	try {
		while (true) {
			if (DateTime.Now > deadline)
				throw new InvalidOperationException($"The event did not open within {timeLimitMinutes} minutes ({polls} polls).");

			if (inFlight.Count < maxInFlight && DateTime.Now >= nextStart) {
				inFlight.Add(PollOnceAsync(++polls));
				nextStart = DateTime.Now.AddMilliseconds(intervalMs);
			}

			// Wake for whichever comes first: a poll answering, the time to start the next poll (only if there is room), or Control-C.
			var waitFor = new List<Task>(inFlight) { cancelled };
			if (inFlight.Count < maxInFlight)
				waitFor.Add(Task.Delay(Math.Max(1, (int)(nextStart - DateTime.Now).TotalMilliseconds)));
			await Task.WhenAny(waitFor);
			stop.Token.ThrowIfCancellationRequested();

			foreach (var done in inFlight.Where(t => t.IsCompleted).ToList()) {
				inFlight.Remove(done);
				var (number, events) = done.Result;
				if (events == null) continue;	// failed; already reported
				VowEvent? current = events.FirstOrDefault(e => e.Uuid == uuid);
				if (current == null)
					throw new InvalidOperationException($"The selected event {uuid} is no longer in the list.");
				// After registration ends (seen 2026-10-01 at 10:02:29) the status is "closed" and register_url is gone again; it does not reopen.
				if (current.Status == "closed")
					throw new InvalidOperationException($"\"{current.Name}\" is closed. Nothing to wait for.");
				if (current.JourneyId != 0) {
					context.Events = events;
					context.SelectedEvent = current;
					Console.WriteLine($"   open in poll {number} (of {polls} started): status {current.Status}, journey {current.JourneyId}");
					return;
				}
			}
		}
	}
	finally {
		ctrlCStopsCleanly = false;
		pollStop.Cancel();
	}
}

async Task Step4_LoadJourney(Context context) {
	// ---- Step 4: load the journey (what the registration page does on load). ----
	// GET /api/v2/events/{uuid}/journeys/{journey_id}/load-for-visitor   (docs/VOW_SNL_FLOW.md 5.2)
	// - Sets the Laravel cookies (XSRF-TOKEN, vow_session, ...) that later calls send back; the cookie jar keeps them.
	// - Gives the ids we need: landing step, RSVP step, the "Continue" action, and max_plus_ones (guest limit).
	// Step ids change every week (they belong to the journey), so they are read here and never hard-coded.
	VowEvent selected = context.SelectedEvent!;
	string json = await SendWithRetryAsync("4. load journey",
		() => ApiRequest(HttpMethod.Get, $"{Vow.ApiBase}/api/v2/events/{selected.Uuid}/journeys/{selected.JourneyId}/load-for-visitor", Vow.GoOrigin),
		TimeSpan.FromMilliseconds(JourneyTimeoutMs), JourneyBudgetSeconds);
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
	// - Never hedged (no duplicates in flight). Retried only after a 5xx or a network error, up to RsvpMaxAttempts tries RsvpRetryDelayMs apart, and
	//   not when x-ratelimit-remaining is below RsvpMinRateRemaining: the endpoint is rate limited (x-ratelimit-limit: 10 per window, window and key
	//   unknown), every try counts against it, and a retry of a request that may have succeeded could double-register (the site says only the
	//   latest request counts for a date, but that is not proven here). 4xx answers (422 full, 429 rate limited, ...) are never retried.
	// - 422 {"error":"This event is full.","capacity_full":true,"capacity_step_id":N} means sold out (seen in the capture).
	// - 429 would mean the rate limit was hit (not seen). Do not retry immediately; wait for Retry-After.
	// - Success (2xx) was never captured, so the body is printed as-is.
	VowEvent selected = context.SelectedEvent!;
	Reply reply;
	for (int attempt = 1; ; attempt++) {
		// a request can only be sent once, so each try builds a new one
		var request = ApiRequest(HttpMethod.Put, $"{Vow.ApiBase}/api/v2/events/{selected.Uuid}/attendees/rsvp", Vow.GoOrigin,
			json: context.GetRsvpJson(), useSocketId: true);
		try {
			reply = await SendAsync("8. submit RSVP", request);
		}
		catch (Exception ex) when (attempt < RsvpMaxAttempts && IsRetryable(ex)) {
			Console.WriteLine($"   RSVP try {attempt} of {RsvpMaxAttempts} got no answer ({ex.Message}); it may or may not have reached the server. Trying again in {RsvpRetryDelayMs} ms");
			await Task.Delay(RsvpRetryDelayMs);
			continue;
		}
		if ((int)reply.Status < 500 || attempt >= RsvpMaxAttempts)
			break;
		if (int.TryParse(reply.RateRemaining, out int left) && left < RsvpMinRateRemaining) {
			Console.WriteLine($"   RSVP try {attempt} returned {(int)reply.Status}, but only {left} requests are left in the rate limit (less than {RsvpMinRateRemaining}): not trying again.");
			break;
		}
		Console.WriteLine($"   RSVP try {attempt} of {RsvpMaxAttempts} returned {(int)reply.Status}; trying again in {RsvpRetryDelayMs} ms");
		await Task.Delay(RsvpRetryDelayMs);
	}

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
// Sent only in mode 3; a failure is logged and ignored.

async Task Step2_OpenRegistrationPage(Context context) {
	// ---- Step 2: open the registration page (the register_url the Register button links to). ----
	// GET https://go.vow.app/event/{uuid}/journeys/{journey_id}  -> the Nuxt single-page-app shell. Only load balancer cookies are set.
	VowEvent selected = context.SelectedEvent!;
	await RunOptionalAsync("2. open registration page", async () => {
		var request = new HttpRequestMessage(HttpMethod.Get, $"{Vow.GoOrigin}/event/{selected.Uuid}/journeys/{selected.JourneyId}") {
			Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
		};
		AddBrowserHeaders(request);
		request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/apng,*/*;q=0.8");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
		request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
		request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
		Require(await SendAsync("2. open registration page", request, TimeSpan.FromMilliseconds(OptionalTimeoutMs)));
	});
}

async Task Step3_CheckAuthUser(Context context) {
	// ---- Step 3: ask who is logged in. ----
	// GET /api/auth/user -> 401 {"message":"Unauthenticated."} for an anonymous visitor. That is the expected answer here.
	// POSSIBLE EARLY CALL: the URL has no event or journey and an anonymous visitor always gets 401, so this could be sent before the
	// event opens (during the polling). Its only side effect is setting the Laravel cookies, which last 15 days. Kept after the opening
	// for now because that is the browser's order (a server comparing timestamps could notice the difference). Untested either way.
	await RunOptionalAsync("3. check auth user", async () => {
		await SendAsync("3. check auth user (401 expected)", ApiRequest(HttpMethod.Get, $"{Vow.ApiBase}/api/auth/user", Vow.GoOrigin), TimeSpan.FromMilliseconds(OptionalTimeoutMs));
	});
}

async Task Step5_ConnectPusher(Context context) {
	// ---- Step 5: open the Pusher WebSocket and read the socket id. ----
	// POSSIBLE EARLY CALL: the socket id comes from Pusher, not vow.app, and has nothing to do with the event, so this could be sent
	// before the event opens. Catch: Pusher closes the socket after activity_timeout (120 s) without traffic, and this method never
	// answers keep-alives, so an early connection must be made less than ~2 minutes before the RSVP, or must send {"event":"pusher:ping"}
	// (and answer pings). Whether the server checks that the id belongs to a live socket is unknown (a made-up id was accepted on a full event).
	// Kept after the opening for now, in the browser's order.
	// docs/VOW_SNL_FLOW.md 5.6. The first message from the server is
	//   {"event":"pusher:connection_established","data":"{\"socket_id\":\"1677674.4468990\",\"activity_timeout\":120}"}
	// The socket id is sent as X-Socket-ID on the log-interaction and RSVP calls (Laravel uses it to skip the sender when it broadcasts).
	// No channel is subscribed to in the capture, so the socket only needs to stay open; it is disposed when the script ends.
	if (Vow.IsReplay) { Console.WriteLine("5. connect pusher: skipped (the replay server has no Pusher; no X-Socket-ID is sent)"); return; }
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

// Finds the config file: as given, with ".json" added, then the same two in the credentials folder (relative to the current directory).
string? FindConfigFile(string name) {
	string withJson = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json";
	string[] candidates = [name, withJson, Path.Combine("credentials", name), Path.Combine("credentials", withJson)];
	return candidates.FirstOrDefault(File.Exists);
}

// The next Thursday (today if it is Thursday) at 10:00 local time plus msOffset, which may be negative (start before 10:00).
DateTime GetNextThursday10Am(int msOffset) {
	DateTime now = DateTime.Now;
	int daysFromNow = ((7 + (int)DayOfWeek.Thursday - (int)now.DayOfWeek) % 7);
	DateTime targetDate = now.Date.AddDays(daysFromNow);
	return targetDate.AddHours(10).AddMilliseconds(msOffset);
}

void WriteWarning(string message) {
	Console.ForegroundColor = ConsoleColor.Red;
	Console.Write("   WARNING: ");
	Console.ResetColor();
	Console.WriteLine(message);
}

// Replay server only (--open-show-in): GET /__replay/open-in/N. The reply says when the show opens and the new ids.
async Task OpenShowInAsync(double seconds) {
	string path = $"/__replay/open-in/{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
	string label = $"0. open the replay server's show in {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s";
	using var request = new HttpRequestMessage(HttpMethod.Get, Vow.ApiBase + path);
	DateTime opensAt = DateTime.Now.AddSeconds(seconds);   // when the server will open the show (it counts from the moment it gets the request)
	using var response = await http.SendAsync(request);
	string body = await response.Content.ReadAsStringAsync();
	Console.WriteLine($"{label}: GET {path} -> {(int)response.StatusCode}");
	Console.WriteLine($"   {Excerpt(body, 400)}");
	LogLn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {label}: {(int)response.StatusCode} {body}");
	if (!response.IsSuccessStatusCode)
		throw new InvalidOperationException($"The replay server did not accept {path}: {(int)response.StatusCode} {Excerpt(body, 200)}");
	// the run time becomes this open time, less the usual lead, so the wait for the run time (the "Run In:" countdown) counts down to it
	runTime = opensAt.AddMilliseconds(-StartBeforeOpenMs);
	Console.WriteLine($"   run time set to {runTime:HH:mm:ss.fff}; the show opens at {opensAt:HH:mm:ss.fff}");
	LogLn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} run time set to {runTime:HH:mm:ss.fff}; the show opens at {opensAt:HH:mm:ss.fff}");
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
	// HTTP/2 (falls back to HTTP/1.1 if the server refuses); see the HttpClientHandler setup.
	var request = new HttpRequestMessage(method, url) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
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
	entry.AppendLine($"HTTP/{response.Version} {(int)response.StatusCode} {response.StatusCode}");
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
// timeout: give up if there is no complete answer (headers and body) within this time; the failure is a TaskCanceledException with a TimeoutException
//          inside, which IsRetryable counts as retryable. null = HttpClient's own 100 s.
// cancel:  lets the caller call the request off (a poll that is no longer needed); that is an OperationCanceledException, not a timeout.
async Task<Reply> SendAsync(string label, HttpRequestMessage request, TimeSpan? timeout = null, CancellationToken cancel = default) {
	Guid requestId = Guid.NewGuid();
	await LogRequestAsync(requestId, label, request);
	var stopwatch = System.Diagnostics.Stopwatch.StartNew();
	HttpResponseMessage response;
	string body;
	using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
	if (timeout != null) limit.CancelAfter(timeout.Value);
	try {
		response = await http.SendAsync(request, limit.Token);
		body = await response.Content.ReadAsStringAsync(limit.Token);
	}
	catch (OperationCanceledException) when (timeout != null && limit.IsCancellationRequested && !cancel.IsCancellationRequested) {
		var timedOut = new TaskCanceledException($"no answer within {timeout.Value.TotalSeconds:0.#} s", new TimeoutException());
		LogNoResponse(requestId, stopwatch.Elapsed, timedOut);
		throw timedOut;
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

// For READ-ONLY steps only: retries after a 5xx or a network error, one at a time (no duplicates in flight), RetryDelayMs apart, for up to
// RetryBudgetSeconds from the first try. Never use this for the RSVP: it has its own, stricter retry in Step8_SubmitRsvp (rate limit,
// docs/VOW_SNL_FLOW.md 5.4).
async Task<string> SendWithRetryAsync(string label, Func<HttpRequestMessage> makeRequest, TimeSpan? timeout = null, int budgetSeconds = RetryBudgetSeconds) {
	var budget = System.Diagnostics.Stopwatch.StartNew();
	for (int attempt = 1; ; attempt++) {
		try {
			return Require(await SendAsync(label, makeRequest(), timeout)).Body;
		}
		catch (Exception ex) when (IsRetryable(ex) && budget.Elapsed < TimeSpan.FromSeconds(budgetSeconds)) {
			Console.WriteLine($"   attempt {attempt} failed ({ex.Message}); trying again in {RetryDelayMs} ms ({budget.Elapsed.TotalSeconds:0.0} of {budgetSeconds} s used)");
			await Task.Delay(RetryDelayMs);
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
			json: body, useSocketId: true), TimeSpan.FromMilliseconds(OptionalTimeoutMs)));
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
	public static string ApiBase { get; private set; } = "https://api.vow.app";
	/// <summary>The registration single-page app. Sent as Origin/Referer on registration calls.</summary>
	public static string GoOrigin { get; private set; } = "https://go.vow.app";
	/// <summary>The listing single-page app (iframed by snlstandby.nbcuni.com). Sent as Origin/Referer on the events-list call.</summary>
	public static string ProOrigin { get; private set; } = "https://pro.vow.app";
	/// <summary>True after UseReplayServer: the calls go to the local replay server (replay-server/ReplayServer.cs) instead of the real site.</summary>
	public static bool IsReplay { get; private set; }
	/// <summary>The replay server serves the API, the registration page and the listing page from one origin.</summary>
	public static void UseReplayServer(string baseUrl) { ApiBase = GoOrigin = ProOrigin = baseUrl; IsReplay = true; }
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

	// ---- Discovered by step 5 (browser-mimicking, mode 3 only) ----
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
	/// so plus_ones = GroupSize - 1, limited by the RSVP step's options.max_plus_ones. Not confirmed by a success capture.
	/// In mode 1 the journey is never loaded, so the limit is assumed to be 1 (its value on 2026-09-24 and 2026-10-01).
	/// </summary>
	public int GetPlusOnesToRequest() {
		const int AssumedMaxPlusOnes = 1;
		int maxPlusOnes = Journey == null
			? AssumedMaxPlusOnes
			: Journey.Journey.Steps.FirstOrDefault(s => s.Type == "rsvp")?.Options?.MaxPlusOnes ?? 0;
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
	public int GroupSize { get; set; } = 1;
	/// <summary>"forSNL" = wait until the next Thursday 09:59:59 local time, then poll until the show opens; anything else (or missing) = start at once.</summary>
	public string? RunAt { get; set; }
}

public sealed class RunConfig {
	/// <summary>1 = just the RSVP, 2 = load the journey then the RSVP, 3 = copy the browser. See the header.</summary>
	public int Mode { get; set; } = 2;
	public bool Submit { get; set; }
	public bool ListOnly { get; set; }
	public bool Wait { get; set; } = true;
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
	// Not checked with --replay-server: the replay server's shows are the ones from the 2026-10-01 capture, whose start times are in the past.
	[JsonIgnore] public bool HasPassed => !Vow.IsReplay && DateTimeOffset.Parse(StartsAt) < DateTimeOffset.UtcNow;

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
