#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net8.0
#:property PublishAot=false
#:include ConsoleEx.cs
// Local replay of the captured SNL Standby / vow.app sign-up pages (saz/vow/snl_oct_08), for testing SnlStandby.user.js
// when the real servers are not available. Spec: docs/capture-specific-spa-replay-spec.md. The .saz is NOT needed at run time:
// extract_saz_replay.py (in this folder) turned it into replay-data/ (index.json + bodies/).
//
// Log:  everything printed is also written to replay_<date>_<time>.log in the current folder (git-ignored; with dotnet run that is the snl-replay/ folder), with timestamps, the bodies of
//       PUT/POST requests, and the Host/Referer of unmatched requests, for later analysis. Search it for "UNMATCHED" and "SYNTHESIZED".
// Run:  dotnet run snl-replay/SnlReplay.cs -- [--port N] [--open] [--rsvp ok|full] [--email] [--no-load] [--failures] [--help]
//
// The captured traffic used four hosts. They are all served from this ONE origin, with no path prefixes, because their paths do not collide:
//   snlstandby.nbcuni.com  /                                   the page with the iframe          -> http://localhost:PORT/
//   pro.vow.app            /public/nbc, /_nuxt/*               the show list (Nuxt 2)            -> http://localhost:PORT/public/nbc
//   go.vow.app             /event/{uuid}/journeys/{id}, /_nuxt/*   the registration page (Nuxt 3)
//   api.vow.app            /api/..., /sanctum/...              the JSON API (same origin now, so no CORS)
// The extractor already replaced the absolute https://<those hosts> URLs in the HTML/JS/JSON with a placeholder that is filled in
// with http://localhost:PORT at start-up, so the pages call this server. A request is matched by method + path + exact query. Request.Host is only used to pick the captured host
// when it is one of the original names (e.g. a hosts-file setup); otherwise every captured host is tried.
// Event ids (guids) and journey ids are NOT the captured ones: each start makes new random ones for both shows (journey ids 1000 to 4000) and
// prints them at start-up; see "shows" below.
// Journey step and action ids are NOT the captured ones either: each show gets its own, made up the first time each captured id is met (steps
// 20000 to 59999, actions 60000 to 99999; new ones at every start / reset) and put into every text body of the show (JSON and the page's
// step=N / data-to="N links).
// A log-interaction whose step_id / action_id the server did not issue for that show is a 400. The confirmation email also needs it to be the rsvp_yes action.
// Repeated identical requests get their captured responses in order; the last one repeats forever (state resets on restart).
//
// EXCEPTION: GET /api/v2/public/by-url/nbc/events (the show list the page and the userscript poll) is NOT replayed from the capture.
// It follows a timeline: the captured coming-soon response until the open time (by default the next 10:00 local time, today's if it is before
// 10:00 now and tomorrow's otherwise, on any day of the week; a client brings it sooner with /__replay/open-in/N), then the captured open
// response as a template, and after ClosedAfterOpen the same with status "closed" and no register_url (that is how the
// closed responses in the Oct 1 vow logs look). No seat counts: the real responses have none.
//
// RSVP (--rsvp ok, the default): PUT /api/v2/events/{uuid}/attendees/rsvp answers 200 with the SUCCESS captured on 2026-10-08
// (saz/vow/snl_oct_08/snl_oct_08.saz, a browser registration that got the confirmation email), as long as the booking number (see below, from
// the time since the show opened) is 300 or less; after that it answers the captured 422 "This event is full.". --rsvp full always answers the 422.
// The captured success is a TEMPLATE (roles rsvp-success and registered-journey in replay-data): per RSVP the server swaps in new attendee ids,
// the booking number (registration_sequence_number), pass codes, registration reference, the typed name and email, the time, and the event / journey
// ids of the show registered for. plus_ones = N in the request creates the primary attendee and N "Guest Of <name>" attendees, as the real server did.
// The page then reloads the journey with GET load-for-visitor?attendee=ID (also the capture: "me" and "attendees" filled in, the Confirmation step
// showing the registration reference and the typed name) and goes to the Confirmation step.
// THE EMAIL: the real server sent the confirmation email only after the RSVP AND the calls that follow it (2026-10-08; a script that sent the
// log-interaction without the attendee id and a null action id got no email): GET load-for-visitor?attendee=ID, then POST log-interaction
// {"attendee_id":ID,"step_id":..,"action_id":<a number>,"session":..}. Only with --email: a synthesized TEST confirmation email is sent (smtp.gmail.com:587,
// as SmtpUser) to the address typed into the form when THAT log-interaction arrives (attendee_id of a registration made here, action_id not null),
// once per registration. Without it nothing is mailed, but the log still says whether the email would have been triggered. Whether the
// load-for-visitor call is also required is not known (the capture had both), so it is only reported, not required.
// --failures: the slow calls below (auth/user, load-for-visitor, media, log-interaction) that arrive during the first 5 seconds after the show
// opened do not succeed after their delay: they wait 3 seconds and answer 500 or 504 (chosen at random for each call). Calls before the open,
// and calls 5 or more seconds after it, behave as usual (under load: the flat wait, then the captured answer). It works with or without --no-load.
// --failures ALSO makes the registration page hang: the first 2 requests for it (GET /event/{uuid}/journeys/{id}) after each start or
// /__replay/open-in/N get no answer at all. The server holds them until the client gives up (a script that asks again cancels the old request),
// then answers the later ones as usual.
// Under load is the DEFAULT; --no-load turns it off.
// Under load the server copies what the 2026-10-01 go-live looked like. The calls that were slow in the capture between 10:00:25 and 10:00:47
// (GET /api/auth/user, load-for-visitor, GET /api/v2/media/*, log-interaction) each wait a flat 5 seconds before they are answered. The
// show list, the page's files and the RSVP itself (0.18 s at 10:01:00 in the capture) stay fast. The delays are logged.
// Test control: GET /__replay/open-in/N (N seconds, may be fractional) moves the open time to N seconds from now: the list goes back to
// coming_soon, flips to open when N has passed, and closes ClosedAfterOpen later. It also starts everything afresh: new random event and journey
// ids for both shows, repeated-response counters back at the first response, and the attendees from earlier tests forgotten. The exact flips are logged as "*** FLIP ... ***".
// Each change is logged as "TEST CONTROL: ..." and its new open time ("opens in N s at HH:mm:ss.fff") is shown in ORANGE on the console (not in the log file).
// The booking number (event.next_registration_sequence_number) is 1 when the show opens and grows by 10 per second; above 300 the RSVP is "full".
// The Gmail app password is NOT in this file (it is tracked by git): put it alone on one line in credentials/replay-smtp.txt (git-ignored,
// found by walking up from the current folder) or in the environment variable REPLAY_SMTP_KEY. Without it the mail is skipped and logged.
//
// Not replayed (404 or failing silently): the Pusher WebSocket, fonts.googleapis.com, FullStory, Google Analytics -- those were not
// extracted. The page keeps working without them.

using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// ---- show-list timeline ----
var ClosedAfterOpen = TimeSpan.FromMinutes(3);
var UnderLoadDelay = TimeSpan.FromSeconds(5);   // how long each slow call waits under load
var FailureWindow = TimeSpan.FromSeconds(5);    // --failures: calls that arrive less than this long after the show opened fail
int HangPageRequests = 2;                        // --failures: how many registration page requests get no answer
var FailureDelay = TimeSpan.FromSeconds(3);     // --failures: how long such a call waits before the 500 / 504
const string EventsPath = "/api/v2/public/by-url/nbc/events";

const string SmtpUser = "rettigcd@gmail.com";

// --help: every option, one per line, in alphabetical order
string[] helpLines = {
	"Replay server for the SNL Standby / vow.app pages. Usage: SnlReplay [options]",
	"",
	"  --email          Send a synthesized TEST confirmation email for each registration, when the log-interaction that follows its RSVP arrives",
	"                   (smtp.gmail.com; the key is read from credentials/replay-smtp.txt or the REPLAY_SMTP_KEY variable). Without it no email is sent.",
	"  --failures       Make things fail: in the first 5 s after the show opens, the slow calls (auth/user, load-for-visitor, media,",
	"                   log-interaction) wait 3 s and answer 500 or 504, and the first 2 requests for the registration page get no answer.",
	"  --help           Show this list and exit.",
	"  --no-load        Turn the under-load delays off. By default those slow calls each wait a flat 5 s before they are answered.",
	"  --open           Open the site in the default browser when the server starts.",
	"  --port N         Listen on port N. Without it: port 50219, or another free port if 50219 is in use.",
	"  --rsvp ok|full   How the RSVP is answered. ok (the default): success, with a booking number that grows with the time since the show",
	"                   opened (sold out after 300). full: every RSVP gets the captured \"event is full\" answer (422).",
	"",
	"Test control while running: GET /__replay/open-in/N sets the show to open N seconds from now (and creates new ids).",
};
int? port = null;
bool rsvpOk = true;   // default: --rsvp ok
bool open = false;
bool sendEmail = false;   // --email
bool underLoad = true;   // default; --no-load turns it off
bool failures = false;   // --failures
for (int i = 0; i < args.Length; i++) {
	if (args[i] == "--help") { foreach (var line in helpLines) Console.WriteLine(line); return; }
	else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
	else if (args[i] == "--open") open = true;
	else if (args[i] == "--email") sendEmail = true;
	else if (args[i] == "--no-load") underLoad = false;
	else if (args[i] == "--failures") failures = true;
	else if (args[i] == "--rsvp" && i + 1 < args.Length && args[i + 1] is "ok" or "full") rsvpOk = args[++i] == "ok";
	else { Console.WriteLine("Usage: SnlReplay [--port N] [--open] [--rsvp ok|full] [--email] [--no-load] [--failures]   (rsvp defaults to ok; no email is sent unless --email; the slow calls are on unless --no-load)");
		Console.WriteLine("Run with --help for a description of each option."); return; }
}
if (port == null) {   // no --port: try the default port, and if something else is using it, any free port (picked by the system)
	const int DefaultPort = 50219;
	foreach (int candidate in new[] { DefaultPort, 0 }) {
		try {
			var probe = new TcpListener(IPAddress.Loopback, candidate);
			probe.Start();
			port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			break;
		} catch (SocketException) { /* in use: try the next one */ }
	}
	if (port == null) { Console.WriteLine("No free port found."); return; }
}

// replay-data is in the snl-replay folder, next to this file. It is looked for from the current folder and every folder above it (as
// <folder>/snl-replay/replay-data, or <folder>/replay-data), so it is found from the repo root, from snl-replay/ or from any folder below the root.
string? dataDir = null;
for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null && dataDir == null; dir = dir.Parent)
	dataDir = new[] { Path.Combine(dir.FullName, "snl-replay", "replay-data"), Path.Combine(dir.FullName, "replay-data") }.FirstOrDefault(Directory.Exists);
if (dataDir == null) { Console.WriteLine("replay-data folder not found (it is in the snl-replay folder; run from the repo root or from snl-replay/)."); return; }

var captured = JsonSerializer.Deserialize<List<Captured>>(File.ReadAllText(Path.Combine(dataDir, "index.json")),
	new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
string logPath = Path.Combine(Directory.GetCurrentDirectory(), $"replay_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
// Everything written through ConsoleEx.WriteLine goes to the console and, with the time in front, to the log file. {Fg.DarkYellow}...{Fg.Restore} in an
// interpolated string draws just that part in a colour on the console (ConsoleEx.cs); the log file gets the plain text.
ConsoleEx.LogPath = logPath;
ConsoleEx.TimeStamp = true;

// The two shows as captured on 2026-10-08. Every start, and every call of /__replay/open-in/N, gives each show a NEW random event id (guid) and
// journey id (1000 to 4000), so a script can not rely on ids that are fixed between runs. They are swapped in below, in the request paths and in the text bodies, in exactly
// these forms: the event guid, journeys/ID (also JSON-escaped journeys\/ID), "journey_id":ID and "journey":{"id":ID. Other numbers that happen
// to equal a journey id (step and action ids etc.) are not touched.
var shows = new[] {
	// start times as in the captured show list ("2026-10-10T20:00:00Z" and "2026-10-10T23:30:00Z"); the Live Show's end is as captured in its journey,
	// the Dress Rehearsal's (never captured) is the same length after its start
	new Show("Dress Rehearsal", "a4ca1ec9-c1bc-491e-9aee-9d39b00c17eb", 1404, new DateTime(2026, 10, 10, 20, 0, 0), new DateTime(2026, 10, 10, 20, 29, 0)),
	new Show("Live Show", "43f0d714-9761-430e-8685-dca1bd0b8490", 1406, new DateTime(2026, 10, 10, 23, 30, 0), new DateTime(2026, 10, 10, 23, 59, 0)),
};
string ReplaceIds(string text, Show from, string toEventId, int toJourneyId) =>
	text.Replace(from.EventId, toEventId)
		.Replace($"journeys/{from.JourneyId}", $"journeys/{toJourneyId}")
		.Replace($"journeys\\/{from.JourneyId}", $"journeys\\/{toJourneyId}")
		.Replace($"\"journey_id\":{from.JourneyId}", $"\"journey_id\":{toJourneyId}")
		.Replace($"\"journey\":{{\"id\":{from.JourneyId}", $"\"journey\":{{\"id\":{toJourneyId}");
string Renumber(string text) {
	foreach (var show in shows) text = ReplaceIds(text, show, show.NewEventId, show.NewJourneyId);
	return text;
}
// Journey step / action ids as captured on 2026-10-08 (the Live Show's journey; the Dress Rehearsal gets a copy of it).
int[] CapturedStepIds = { 6280, 6281, 6282, 6283 }, CapturedActionIds = { 4875, 4876, 4877 };
const int RsvpYesAction = 4876;   // captured id of the rsvp_yes action (the one that leads to the Confirmation step)

// The id this show uses for a captured step / action id; made up the first time it is asked for.
int Issue(Show show, int capturedId) {
	lock (show.IssuedIds) {
		if (show.IssuedIds.TryGetValue(capturedId, out int issued)) return issued;
		bool isStep = CapturedStepIds.Contains(capturedId);
		int lo = isStep ? 20000 : 60000;
		do issued = Random.Shared.Next(lo, lo + 40000); while (show.IssuedIds.ContainsValue(issued));
		return show.IssuedIds[capturedId] = issued;
	}
}
string ReplaceNumbers(string text, int[] from, Func<int, int> to) {
	foreach (int id in from) text = System.Text.RegularExpressions.Regex.Replace(text, $@"(?<!\d){id}(?!\d)", _ => to(id).ToString());
	return text;
}
// the captured step / action ids in a text body -> this show's
string IssueJourneyIds(Show show, string text) => ReplaceNumbers(text, CapturedStepIds.Concat(CapturedActionIds).ToArray(), id => Issue(show, id));
bool IsText(Captured c) => c.ContentType.StartsWith("text/") || c.ContentType.StartsWith("application/json") || c.ContentType.StartsWith("application/javascript");

string origin = $"http://localhost:{port}";
foreach (var c in captured) {
	c.Body = File.ReadAllBytes(Path.Combine(dataDir, c.File));
	// the extractor replaced every https://<original host> in text bodies with this placeholder
	if (IsText(c)) c.Body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(c.Body).Replace("__REPLAY_ORIGIN__", origin));
}

var dress = shows[0];
var live = shows[1];
// The text that is specific to a show, turned from the captured show's (the Live Show) into `to`'s: "Live Show" / "LIVE SHOW" become "Dress Rehearsal" /
// "DRESS REHEARSAL", and the start time (taken from the captured show list) and the end time (kept the same length after it) move to the new show's.
// Nothing else is known to differ, so nothing else is changed (the arrival-time options and event code are still the Live Show's).
string ShowSpecific(string text, Show from, Show to) => from == to ? text : text
	.Replace(from.Name, to.Name).Replace(from.Name.ToUpperInvariant(), to.Name.ToUpperInvariant())
	.Replace($"\"starts_at\":\"{from.Start:yyyy-MM-dd HH:mm:ss}\"", $"\"starts_at\":\"{to.Start:yyyy-MM-dd HH:mm:ss}\"")
	.Replace($"\"ends_at\":\"{from.End:yyyy-MM-dd HH:mm:ss}\"", $"\"ends_at\":\"{to.End:yyyy-MM-dd HH:mm:ss}\"")
	.Replace($"\"start_date_formatted\":\"{from.Start:MMMM dd, yyyy}\"", $"\"start_date_formatted\":\"{to.Start:MMMM dd, yyyy}\"")
	.Replace($"\"start_time_formatted\":\"{from.Start:h:mmtt}\"", $"\"start_time_formatted\":\"{to.Start:h:mmtt}\"");

// Only the Live Show's registration side was captured (page, load-for-visitor, log-interaction, rsvp, and the success templates below). The Dress
// Rehearsal gets a copy of all of it, with the Dress Rehearsal's ids and its own show-specific text (ShowSpecific).
foreach (var c in captured.Where(c => c.Path.Contains(live.EventId)).ToList()) {
	var copy = c.Copy();
	copy.Path = ReplaceIds(c.Path, live, dress.EventId, dress.JourneyId);
	if (IsText(c)) copy.Body = Encoding.UTF8.GetBytes(ShowSpecific(ReplaceIds(Encoding.UTF8.GetString(c.Body), live, dress.EventId, dress.JourneyId), live, dress));
	captured.Add(copy);
}

// "captured" now holds the data with the CAPTURED ids. BuildSnapshot makes the data the server answers from, with new random ids (see above).
var buildLock = new object();
Snapshot BuildSnapshot() {
	lock (buildLock) {
		foreach (var show in shows) {
			show.NewEventId = Guid.NewGuid().ToString();
			show.IssuedIds = new();
			do show.NewJourneyId = Random.Shared.Next(1000, 4001); while (shows.Any(o => o != show && o.NewJourneyId == show.NewJourneyId));
		}
		var copies = captured.Select(c => {
			var copy = c.Copy();
			copy.Path = Renumber(c.Path);
			if (IsText(c)) {
				string text = Encoding.UTF8.GetString(c.Body);
				var ofShow = shows.FirstOrDefault(sh => c.Path.Contains(sh.EventId));   // the show's own page / journey / RSVP answers (not the JS files)
				if (ofShow != null) text = IssueJourneyIds(ofShow, text);
				copy.Body = Encoding.UTF8.GetBytes(Renumber(text));
			}
			return copy;
		}).ToList();
		var entries = copies.Where(c => c.Role == "").ToList();
		// identity "METHOD host/path?query" -> its responses in captured order
		var sequences = new Dictionary<string, Sequence>();
		foreach (var c in entries) {
			string key = Key(c.Method, c.Host, c.Path, c.Query);
			if (!sequences.TryGetValue(key, out var seq)) sequences[key] = seq = new Sequence();
			seq.Responses.Add(c);
		}
		return new Snapshot(sequences, entries.Select(c => c.Host).Distinct().ToList(), entries,
			copies.Single(c => c.Role == "events-coming-soon"), copies.Single(c => c.Role == "events-open"),
			shows.Select(sh => (sh.NewEventId, sh.NewJourneyId)).ToList());
	}
}
void LogShowIds() {
	foreach (var show in shows) ConsoleEx.WriteLine($"   {show.Name + ":",-20} event {show.NewEventId}  journey {show.NewJourneyId}");
}
Snapshot state = BuildSnapshot();
// The default open time: the next 10:00 local time, today's if it is before 10:00 now and tomorrow's otherwise (any day of the week). In practice
// a client sets a sooner time for its test with /__replay/open-in/N.
DateTime NextTenAm() {
	var now = DateTime.Now;
	var ten = new DateTime(now.Year, now.Month, now.Day, 10, 0, 0, DateTimeKind.Local);
	return ten <= now ? ten.AddDays(1) : ten;
}
long openAtTicks = NextTenAm().ToUniversalTime().Ticks;   // when the show list flips to open (moved by /__replay/open-in/N)
DateTime OpenAt() => new DateTime(Interlocked.Read(ref openAtTicks), DateTimeKind.Utc);
string lastEventsState = "";
int nextAttendeeId = 900000;
// "Booking Number" on the Confirmation page = event.next_registration_sequence_number: 1 when the show opens, +10 per whole second after that.
// Numbers above MaxBookingNumber are not allowed, so after that the RSVP gets the captured "event is full" 422.
const int MaxBookingNumber = 300;
int BookingNumberNow() => 1 + 10 * Math.Max(0, (int)(DateTime.UtcNow - OpenAt()).TotalSeconds);

// load-for-visitor (the journey, not yet registered) with the seat numbers on the same clock as the booking number: the next booking number is
// BookingNumberNow(), so BookingNumberNow() - 1 seats are taken (at most journey.capacity), and the journey is over capacity once the RSVP is sold out
// (a number above MaxBookingNumber). The captured response was taken after the real show had sold out (capacity 305, signup_count 305,
// is_over_capacity true, next_registration_sequence_number 303), so without this a call at the open already looked full. Only these three fields change.
void ApplyBookingClock(JsonObject journey) {
	int next = BookingNumberNow();
	var inner = journey["journey"]!.AsObject();
	inner["signup_count"] = Math.Min(next - 1, inner["capacity"]!.GetValue<int>());
	journey["is_over_capacity"] = next > MaxBookingNumber;
	journey["event"]!["next_registration_sequence_number"] = next;
}
int pageRequests = 0;   // --failures: registration page requests since the last start or reset
var registrations = new System.Collections.Concurrent.ConcurrentDictionary<string, Registration>();   // successful RSVPs made here, by the primary attendee's id

// The captured successful RSVP (see the header) is the Live Show's. Its text has the captured event uuid and journey id, which MakeRegistration swaps.
var capturedShow = live;
string TemplateText(string role) => Encoding.UTF8.GetString(captured.Single(c => c.Role == role).Body);

string RandomText(int length, string alphabet) => new string(Enumerable.Range(0, length).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]).ToArray());

// The RSVP answer for a new registration: a primary attendee and `guests` plus-ones, made from the captured two attendees. Returns the registration
// (kept for the load-for-visitor and log-interaction calls that follow) and the answer's text.
(Registration, string) MakeRegistration(string eventId, int journeyId, string first, string last, string email, int guests, int booking) {
	var reply = (JsonObject)JsonNode.Parse(TemplateText("rsvp-success"))!;
	var created = reply["attendees"]!["created"]!.AsArray();
	var tplPrimary = (JsonObject)created[0]!;
	var tplGuest = (JsonObject)created[1]!;
	string tplName = tplPrimary["full_name"]!.GetValue<string>();
	string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
	int firstId = Interlocked.Add(ref nextAttendeeId, guests + 1) - guests;
	string fullName = $"{first} {last}";
	JsonObject Make(JsonObject tpl, int index) {
		string text = tpl.ToJsonString();
		string tplId = tpl["id"]!.ToString(), tplPass = tpl["pass_code"]!.GetValue<string>(), tplRef = tpl["registration_reference"]!.GetValue<string>();
		text = text.Replace(tplPass, RandomText(20, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"))   // the pass links contain the pass code,
			.Replace(tplRef, RandomText(11, "ABCDEFGHIJKLMNOPQRSTUVWXYZ"))                                                  // the QR link the attendee id
			.Replace($"attendees/{tplId}/", $"attendees/{firstId + index}/");
		var a = (JsonObject)JsonNode.Parse(ShowSpecific(ReplaceIds(text, capturedShow, eventId, journeyId), capturedShow, shows.First(sh => sh.NewEventId == eventId)))!;
		a["id"] = firstId + index;
		a["parent_attendee_id"] = firstId;
		a["registration_sequence_number"] = booking + index;
		a["registration_journey_id"] = journeyId;
		a["rsvped_at"] = now; a["created_at"] = now; a["updated_at"] = now;
		string f = index == 0 ? first : "Guest Of", l = index == 0 ? last : fullName;
		a["first_name"] = f; a["last_name"] = l; a["full_name"] = $"{f} {l}";
		var details = a["user_details"]!.AsObject();
		details["first_name"] = f; details["last_name"] = l; details["full_name"] = $"{f} {l}";
		if (index == 0) { a["email"] = email; details["email"] = email; }
		return a;
	}
	var family = Enumerable.Range(0, guests + 1).Select(i => Make(i == 0 ? tplPrimary : tplGuest, i)).ToList();
	created.Clear();
	foreach (var a in family) created.Add(JsonNode.Parse(a.ToJsonString()));
	var log = reply["activity_logs"]!["created"]!.AsArray()[0]!.AsObject();
	log["id"] = firstId;
	log["reporter"] = log["reporter"]!.GetValue<string>().Replace(tplName, fullName);
	log["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.000000'Z'");
	return (new Registration(family, eventId, journeyId, booking), reply.ToJsonString());
}

// The journey as the real server answers load-for-visitor?attendee=ID after the RSVP: the captured one with the new attendees and numbers.
string RegisteredJourney(Registration reg) {
	var primary = reg.Attendees[0];
	var show = shows.First(sh => sh.NewEventId == reg.EventId);
	string text = IssueJourneyIds(show, ShowSpecific(ReplaceIds(TemplateText("registered-journey"), capturedShow, reg.EventId, reg.JourneyId), capturedShow, show))
		.Replace("attendee=398341", $"attendee={primary["id"]}");
	var journey = (JsonObject)JsonNode.Parse(text)!;
	journey["me"] = JsonNode.Parse(primary.ToJsonString());
	journey["attendees"] = new JsonArray(reg.Attendees.Select(a => JsonNode.Parse(a.ToJsonString())).ToArray());
	int taken = reg.Booking - 1 + reg.Attendees.Count;   // seats taken once they are in
	var inner = journey["journey"]!.AsObject();
	journey["event"]!["next_registration_sequence_number"] = reg.Booking + reg.Attendees.Count;
	journey["event"]!["total_attendees"] = journey["event"]!["total_attendees"]!.GetValue<int>() - inner["signup_count"]!.GetValue<int>() + taken;
	inner["signup_count"] = Math.Min(taken, inner["capacity"]!.GetValue<int>());
	// The Confirmation step shows the registration reference, first name and last name in bold; the capture has the captured visitor's, so swap those
	string Enc(string v) => System.Net.WebUtility.HtmlEncode(v);
	foreach (var step in inner["steps"]!.AsArray()) {
		if (step!["content"]?["html"] == null) continue;
		step["content"]!["html"] = step["content"]!["html"]!.GetValue<string>()
			.Replace(">YQSIESJLKTI<", $">{Enc(primary["registration_reference"]!.GetValue<string>())}<")
			.Replace("bold;\">Test<", $"bold;\">{Enc(primary["first_name"]!.GetValue<string>())}<")
			.Replace("bold;\">User<", $"bold;\">{Enc(primary["last_name"]!.GetValue<string>())}<");
	}
	return journey.ToJsonString();
}

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://localhost:{port}");
var app = builder.Build();

app.Use(async (http, next) => {
	// request bodies of PUT/POST/etc. go to the log, so what the page sent can be analysed later
	if (http.Request.Method != "GET" && http.Request.Method != "HEAD") {
		http.Request.EnableBuffering();
		using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true);
		string text = await reader.ReadToEndAsync();
		http.Request.Body.Position = 0;
		ConsoleEx.WriteLine($"{Fg.Blue}{http.Request.Method}{Fg.Restore} {http.Request.Path}{http.Request.QueryString} body: {(text.Length > 2000 ? text[..2000] + "..." : text)}");
	}
	await next();
});

app.Run(async http => {
	var req = http.Request;
	string method = req.Method == "HEAD" ? "GET" : req.Method;
	string path = req.Path.Value ?? "/";
	string query = req.QueryString.HasValue ? req.QueryString.Value![1..] : "";
	var st = state;   // one request uses one snapshot, even if a reset swaps it meanwhile

	// --failures: the first HangPageRequests requests for the registration page (GET /event/{uuid}/journeys/{id}) get no answer at all. The server
	// holds the request until the client gives up (a browser cancels it when a new navigation starts), then answers the later requests as usual.
	if (failures && method == "GET" && System.Text.RegularExpressions.Regex.IsMatch(path, @"^/event/[0-9a-f-]{36}/journeys/\d+$")) {
		int pageTry = Interlocked.Increment(ref pageRequests);
		if (pageTry <= HangPageRequests) {
			ConsoleEx.WriteLine($"{Fg.Blue}GET{Fg.Restore} {path} {Fg.Red}hangs (registration page request {pageTry} of {HangPageRequests}): no answer until the client gives up{Fg.Restore}");
			try { await Task.Delay(Timeout.Infinite, http.RequestAborted); } catch (OperationCanceledException) { /* the client gave up */ }
			ConsoleEx.WriteLine($"   the client gave up on registration page request {pageTry}");
			return;
		}
	}

	// under load (the default, see --no-load): the calls that were slow at the 2026-10-01 go-live wait a flat 5 s before anything else happens
	if (IsSlowUnderLoad(path)) {
		var sinceOpenNow = DateTime.UtcNow - OpenAt();
		if (failures && sinceOpenNow >= TimeSpan.Zero && sinceOpenNow < FailureWindow) {
			// --failures: in the first seconds after the show opened these calls fail instead of answering
			int code = Random.Shared.Next(2) == 0 ? 500 : 504;
			ConsoleEx.WriteLine($"   failure: {Fg.Blue}{req.Method}{Fg.Restore} {path} arrived {sinceOpenNow.TotalSeconds:0.0} s after the open; waits {FailureDelay.TotalSeconds:0.0} s, then {code}");
			try { await Task.Delay(FailureDelay, http.RequestAborted); } catch (OperationCanceledException) { return; }   // the client gave up
			http.Response.StatusCode = code;
			http.Response.ContentType = code == 500 ? "application/json" : "text/plain";
			await http.Response.WriteAsync(code == 500 ? "{\"message\":\"Server Error\"}" : "Gateway Timeout");
			return;
		}
		if (underLoad) {
			var delay = UnderLoadDelay;
			ConsoleEx.WriteLine($"{Fg.Blue}{req.Method}{Fg.Restore} {path} {Fg.Cyan} under load of {delay.TotalSeconds:0.0} s{Fg.Restore}");
			try { await Task.Delay(delay, http.RequestAborted); } catch (OperationCanceledException) { return; }   // the client gave up
		}
	}

	// test control (not part of the captured site): /__replay/open-in/N sets the open time N seconds from now; the list goes back to coming_soon
	// until then, then open, then closed ClosedAfterOpen later. N may be fractional; 0 opens it now.
	if (path.StartsWith("/__replay/open-in/")) {
		if (!double.TryParse(path["/__replay/open-in/".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double secs) || secs < 0 || secs > 86400) {
			http.Response.StatusCode = 400;
			await http.Response.WriteAsync("Usage: /__replay/open-in/<seconds from now, 0 to 86400>");
			return;
		}
		// a reset starts everything afresh: new random ids for both shows, the repeated-response counters back at the first response,
		// and the attendees / booking numbers of the previous test forgotten (they belong to the old ids)
		state = BuildSnapshot();
		registrations.Clear();
		Interlocked.Exchange(ref pageRequests, 0);   // --failures starts over: the next registration page requests hang again
		Interlocked.Exchange(ref openAtTicks, (DateTime.UtcNow + TimeSpan.FromSeconds(secs)).Ticks);
		string newIds = string.Join("; ", shows.Select(sh => $"{sh.Name} event {sh.NewEventId} journey {sh.NewJourneyId}"));
		string opensAt = OpenAt().ToLocalTime().ToString("HH:mm:ss.fff");
		string msg = $"show list is coming_soon now; opens in {secs} s at {opensAt}; closes {ClosedAfterOpen.TotalMinutes} min after that. New ids: {newIds}";   // the answer to the caller
		ConsoleEx.WriteLine($"TEST CONTROL: show list is coming_soon now; opens in {secs} s at {Fg.DarkYellow}{opensAt}{Fg.Restore}; closes {ClosedAfterOpen.TotalMinutes} min after that. New ids: {newIds}");
		ScheduleFlipLogs();
		http.Response.ContentType = "text/plain";
		await http.Response.WriteAsync(msg);
		return;
	}

	if (method == "GET" && path == EventsPath && query == "") {
		var sinceOpen = DateTime.UtcNow - OpenAt();
		var (body, phase) = EventsAnswer(sinceOpen, st);
		if (phase != lastEventsState) { ConsoleEx.WriteLine($"{Fg.Blue}GET{Fg.Restore} api.vow.app{path} -> {phase} ({sinceOpen.TotalSeconds:+0.0;-0.0}s from open)"); lastEventsState = phase; }
		http.Response.ContentType = st.ComingSoon.ContentType;
		http.Response.Headers["Cache-Control"] = "no-store";
		AppendCookies(http, st.ComingSoon.Cookies);
		http.Response.ContentLength = body.Length;
		if (req.Method != "HEAD") await http.Response.Body.WriteAsync(body);
		return;
	}

	// An RSVP must carry the 8 parts of the captured browser request: rsvp, plus_ones, me, journey, group_id, first_name, last_name, email
	// ("me" and "group_id" may be null but must be there; the others must not be null, and the three text ones not empty), and the event id in
	// the path and the journey id must be the two ids of one of the CURRENT shows (they change on every reset). Anything else is a 400, before
	// any success / "event is full" answer.
	if (method == "PUT" && path.StartsWith("/api/v2/events/") && path.EndsWith("/attendees/rsvp")) {
		string? problem = null;
		try {
			using var parsed = await JsonDocument.ParseAsync(req.Body);
			req.Body.Position = 0;   // the answers below read it again
			if (parsed.RootElement.ValueKind != JsonValueKind.Object) problem = "the body is not a JSON object";
			else {
				var missing = new List<string>();
				foreach (var name in new[] { "rsvp", "plus_ones", "me", "journey", "group_id", "first_name", "last_name", "email" }) {
					bool nullable = name is "me" or "group_id";
					bool textual = name is "first_name" or "last_name" or "email";
					if (!parsed.RootElement.TryGetProperty(name, out var value)
						|| (!nullable && value.ValueKind == JsonValueKind.Null)
						|| (textual && (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))) missing.Add(name);
				}
				if (missing.Count > 0) problem = "missing: " + string.Join(", ", missing);
				else {
					// the event id in the path and the journey id in the body must be the two ids of ONE of the current shows
					string eventId = path.Split('/')[4];
					var match = st.Ids.FirstOrDefault(x => x.EventId == eventId);
					var journey = parsed.RootElement.GetProperty("journey");
					if (match.EventId == null) problem = $"event id {eventId} is not one of the current shows";
					else if (journey.ValueKind != JsonValueKind.Number || !journey.TryGetInt32(out int journeyId) || journeyId != match.JourneyId)
						problem = $"journey id {journey} does not belong to event {eventId}";
				}
			}
		} catch (JsonException) { problem = "the body is not valid JSON"; }
		if (problem != null) {
			ConsoleEx.WriteLine($"{Fg.Blue}PUT{Fg.Restore} api.vow.app{path} -> 400 ({problem})");
			http.Response.StatusCode = 400;
			http.Response.ContentType = "application/json";
			await http.Response.WriteAsync(new JsonObject { ["error"] = "Bad request: " + problem }.ToJsonString());
			return;
		}
	}

	if (rsvpOk && method == "PUT" && path.StartsWith("/api/v2/events/") && path.EndsWith("/attendees/rsvp") && query == "" && BookingNumberNow() <= MaxBookingNumber) {
		using var sent = await JsonDocument.ParseAsync(req.Body);
		string Field(string name) => sent.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
		int guests = sent.RootElement.TryGetProperty("plus_ones", out var po) && po.ValueKind == JsonValueKind.Number && po.TryGetInt32(out int g) ? Math.Clamp(g, 0, 10) : 0;
		var (reg, reply) = MakeRegistration(path.Split('/')[4], sent.RootElement.GetProperty("journey").GetInt32(), Field("first_name"), Field("last_name"), Field("email"), guests, BookingNumberNow());
		string primaryId = reg.Attendees[0]["id"]!.ToString();
		registrations[primaryId] = reg;
		ConsoleEx.WriteLine($"{Fg.Blue}PUT{Fg.Restore} api.vow.app{path} {Fg.Green}-> 200 captured success (attendee {primaryId} + {guests} guest(s), booking number {reg.Booking}, {Field("email")}){Fg.Restore}");
		ConsoleEx.WriteLine($"   the email follows the log-interaction with attendee_id {primaryId} and an action_id{(sendEmail ? "" : " (not sent: start the server with --email)")}");
		http.Response.ContentType = "application/json";
		await http.Response.WriteAsync(reply);
		return;
	}

	if (rsvpOk && method == "GET" && path.EndsWith("/load-for-visitor") && query.StartsWith("attendee=")
			&& registrations.TryGetValue(query["attendee=".Length..], out var known)) {
		known.JourneyLoaded = true;
		ConsoleEx.WriteLine($"{Fg.Blue}GET{Fg.Restore} api.vow.app{path}?{query} -> 200 captured journey for the new attendee {known.Attendees[0]["id"]}");
		http.Response.ContentType = "application/json";
		await http.Response.WriteAsync(RegisteredJourney(known));
		return;
	}

	// log-interaction: step_id and action_id must be ids this server issued for the show in the path (null action_id is allowed: the closed step
	// is logged with none). A number or a numeric string is accepted for action_id (the page sends a string, the confirmation call a number).
	if (method == "POST" && path.StartsWith("/api/v2/events/") && path.EndsWith("/log-interaction")) {
		var ofShow = shows.FirstOrDefault(sh => sh.NewEventId == path.Split('/')[4]);
		string? bad = null;
		try {
			using var logged = await JsonDocument.ParseAsync(req.Body);
			req.Body.Position = 0;
			var root = logged.RootElement;
			if (ofShow == null) bad = "unknown event";
			else if (root.ValueKind != JsonValueKind.Object) bad = "the body is not a JSON object";
			else {
				// null for a missing or JSON null value, -1 for something that is not a number
				long? Number(string name) => root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n
					: v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out long m) ? m : -1 : null;
				long? stepId = Number("step_id"), actionId = Number("action_id");
				var steps = CapturedStepIds.Select(id => (long)Issue(ofShow, id));
				var actions = CapturedActionIds.Select(id => (long)Issue(ofShow, id));
				if (stepId == null || !steps.Contains(stepId.Value)) bad = $"step_id {(stepId == null ? "missing" : stepId.ToString())} is not a step of this show's journey";
				else if (actionId != null && !actions.Contains(actionId.Value)) bad = $"action_id {actionId} is not an action of this show's journey";
			}
		} catch (JsonException) { bad = "the body is not valid JSON"; }
		if (bad != null) {
			ConsoleEx.WriteLine($"{Fg.Blue}POST{Fg.Restore} {path} {Fg.Red}-> 400 ({bad}){Fg.Restore}");
			http.Response.StatusCode = 400;
			http.Response.ContentType = "application/json";
			await http.Response.WriteAsync(new JsonObject { ["error"] = "Bad request: " + bad }.ToJsonString());
			return;
		}
	}

	// The call that makes the real server send the confirmation email: log-interaction with the new attendee's id and an action id (a null action
	// id, as after a full-event 422, does not). The captured answer to log-interaction is still sent afterwards, by the lookup below.
	if (rsvpOk && method == "POST" && path.StartsWith("/api/v2/events/") && path.EndsWith("/log-interaction")) {
		try {
			using var logged = await JsonDocument.ParseAsync(req.Body);
			req.Body.Position = 0;
			var root = logged.RootElement;
			if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("attendee_id", out var aid) && aid.ValueKind == JsonValueKind.Number
					&& registrations.TryGetValue(aid.GetRawText(), out var reg)) {
				bool hasAction = root.TryGetProperty("action_id", out var act) && act.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
				var yesShow = shows.First(sh => sh.NewEventId == reg.EventId);
				if (hasAction && act.ToString() != Issue(yesShow, RsvpYesAction).ToString()) {   // checked above to be an action of the show, but not the one that leads to the Confirmation
					ConsoleEx.WriteLine($"   log-interaction for attendee {aid} with action_id {act}: not the rsvp_yes action ({Issue(yesShow, RsvpYesAction)}), so it does not trigger the confirmation email");
					hasAction = false;
				}
				bool first;
				lock (reg) { first = hasAction && !reg.EmailTriggered; if (first) reg.EmailTriggered = true; }
				if (!hasAction) ConsoleEx.WriteLine($"   log-interaction for attendee {aid}: no rsvp_yes action_id, so no confirmation email");
				else if (!first) ConsoleEx.WriteLine($"   log-interaction for attendee {aid}: the confirmation email was already triggered");
				else {
					ConsoleEx.WriteLine($"   {Fg.Green}log-interaction for attendee {aid} with action_id {act}: triggers the confirmation email{Fg.Restore} (load-for-visitor?attendee={aid} {(reg.JourneyLoaded ? "was" : "was NOT")} called first)");
					if (sendEmail) _ = SendTestConfirmation(reg);
					else ConsoleEx.WriteLine("   no email sent (start the server with --email to send the TEST confirmation)");
				}
			}
		} catch (JsonException) { /* not JSON: the captured answer below is enough */ }
	}

	if (rsvpOk && method == "PUT" && path.EndsWith("/attendees/rsvp") && BookingNumberNow() > MaxBookingNumber)
		ConsoleEx.WriteLine($"   booking number would be {BookingNumberNow()} (> {MaxBookingNumber}): sold out, answering with the captured 'event is full'");

	string hostOnRequest = req.Host.Host;
	var candidates = st.Hosts.Contains(hostOnRequest) ? new List<string> { hostOnRequest } : st.Hosts;
	string? key = candidates.Select(h => Key(method, h, path, query)).FirstOrDefault(st.Sequences.ContainsKey);

	if (key == null) {
		ConsoleEx.WriteLine($"{Fg.Red}404 UNMATCHED: {Fg.Blue}{req.Method}{Fg.Red} {path}{(query == "" ? "" : "?" + query)}   [Host {req.Host}, Referer {req.Headers.Referer}]{Fg.Restore}");
		http.Response.StatusCode = 404;
		return;
	}

	var seq = st.Sequences[key];
	var (resp, n, reused) = seq.Next();
	string what = key[(method.Length + 1)..];
	if (seq.Responses.Count == 1) ConsoleEx.WriteLine($"{Fg.Blue}{req.Method}{Fg.Restore} {what} -> {resp.Status}");
	else ConsoleEx.WriteLine($"{Fg.Blue}{req.Method}{Fg.Restore} {what} -> response {n} of {seq.Responses.Count}{(reused ? " [reused]" : "")} ({resp.Status})");

	http.Response.StatusCode = resp.Status;
	if (resp.ContentType != "") http.Response.ContentType = resp.ContentType;
	http.Response.Headers["Cache-Control"] = "no-store";
	AppendCookies(http, resp.Cookies);
	byte[] responseBody = resp.Body;
	if (rsvpOk && method == "GET" && path.EndsWith("/load-for-visitor") && query == "" && resp.Status == 200) {   // --rsvp full keeps the captured (sold out) numbers
		var journey = (JsonObject)JsonNode.Parse(responseBody)!;
		ApplyBookingClock(journey);
		responseBody = Encoding.UTF8.GetBytes(journey.ToJsonString());
		ConsoleEx.WriteLine($"   seats: {journey["journey"]!["signup_count"]} of {journey["journey"]!["capacity"]} taken, next booking number {journey["event"]!["next_registration_sequence_number"]}{(journey["is_over_capacity"]!.GetValue<bool>() ? " (over capacity)" : "")}");
	}
	http.Response.ContentLength = responseBody.Length;
	if (req.Method != "HEAD") await http.Response.Body.WriteAsync(responseBody);
});

string startUrl = $"http://localhost:{port}/";
await app.StartAsync();
ScheduleFlipLogs();
ConsoleEx.WriteLine($"Replay server running at {Fg.Cyan}{startUrl}{Fg.Restore}");
LogShowIds();
ConsoleEx.WriteLine($"   The show list opens at {Fg.DarkYellow}{OpenAt().ToLocalTime():ddd MMM d HH:mm:ss}{Fg.Restore}. Sets a sooner time with /__replay/open-in/N.");
if (failures) ConsoleEx.WriteLine($"   Failures Enabled (including: the first {HangPageRequests} registration page requests get no answer)");
Console.WriteLine("Ctrl+C to stop.");
if (open) Process.Start(new ProcessStartInfo(startUrl) { UseShellExecute = true });
await app.WaitForShutdownAsync();

// Logs the exact moments the list flips, for measuring how quickly a client noticed. A newer /__replay/open-in/N makes the older timers do nothing.
void ScheduleFlipLogs() {
	long mine = Interlocked.Read(ref openAtTicks);
	async Task At(DateTime when, string what) {
		var wait = when - DateTime.UtcNow;
		if (wait > TimeSpan.Zero) await Task.Delay(wait);
		if (Interlocked.Read(ref openAtTicks) == mine) ConsoleEx.WriteLine($"*** FLIP: show list is now {what} ***");
	}
	_ = At(OpenAt(), "OPEN");
	_ = At(OpenAt() + ClosedAfterOpen, "CLOSED");
}

(byte[] Body, string State) EventsAnswer(TimeSpan sinceOpen, Snapshot st) {
	if (sinceOpen < TimeSpan.Zero) return (st.ComingSoon.Body, "coming_soon");
	if (sinceOpen < ClosedAfterOpen) return (st.OpenTemplate.Body, "open");
	var doc = JsonNode.Parse(st.OpenTemplate.Body)!;
	foreach (var e in doc["events"]!.AsArray()) { e!["status"] = "closed"; e.AsObject().Remove("register_url"); }
	return (Encoding.UTF8.GetBytes(doc.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })), "closed");
}

string? SmtpKey() {
	string? key = Environment.GetEnvironmentVariable("REPLAY_SMTP_KEY");
	for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); string.IsNullOrWhiteSpace(key) && dir != null; dir = dir.Parent) {
		string file = Path.Combine(dir.FullName, "credentials", "replay-smtp.txt");
		if (File.Exists(file)) key = File.ReadAllText(file);
	}
	return string.IsNullOrWhiteSpace(key) ? null : key.Replace(" ", "").Trim();   // Google shows app passwords in groups of 4
}

// Fire-and-forget: a mail problem is logged and never changes the RSVP answer.
async Task SendTestConfirmation(Registration reg) {
	var attendee = reg.Attendees[0];
	string to = attendee["email"]!.GetValue<string>();
	if (to.Equals("test@example.com", StringComparison.OrdinalIgnoreCase)) to = "rettigcd@gmail.com";   // the local userscript's placeholder address goes to the developer instead
	try {
		string? key = SmtpKey();
		if (key == null) { ConsoleEx.WriteLine("   email skipped: no key (credentials/replay-smtp.txt or REPLAY_SMTP_KEY)"); return; }
		if (!to.Contains('@')) { ConsoleEx.WriteLine($"   email skipped: \"{to}\" is not an email address"); return; }
		string uuid = reg.EventId;
		string show = JsonNode.Parse(state.OpenTemplate.Body)!["events"]!.AsArray().FirstOrDefault(e => e!["uuid"]!.GetValue<string>() == uuid)?["name"]?.GetValue<string>() ?? "SNL Standby";
		string nl = Environment.NewLine;
		using var mail = new MailMessage(SmtpUser, to) {
			Subject = $"[TEST] You're registered: {show}",
			Body = "*** TEST *** This message comes from the local replay server (SnlReplay.cs). No real registration was made. ***" + nl + nl
				+ $"Hi {attendee["first_name"]}," + nl + nl + $"You're confirmed for {show}." + nl + nl
				+ $"Booking number: {reg.Booking} (made up)" + nl + $"Attendee id: {attendee["id"]} (made up)" + nl + nl + "*** TEST *** not a real VOW / NBC confirmation ***",
		};
		using var smtp = new SmtpClient("smtp.gmail.com", 587) { EnableSsl = true, Credentials = new System.Net.NetworkCredential(SmtpUser, key) };
		await smtp.SendMailAsync(mail);
		ConsoleEx.WriteLine($"   TEST confirmation email sent to {to}");
	} catch (Exception e) { ConsoleEx.WriteLine($"   TEST confirmation email to {to} FAILED: {e.GetBaseException().Message}"); }
}

// The calls that were slow in the 2026-10-01 capture (4 to 7 s at 10:00:25 to 10:00:47). Not the RSVP, the show list or the static files.
// The captured session cookies carry a different random 40-character name on every response (Laravel's hashed session cookie). Replaying them
// makes the browser pile up a new large localhost cookie per name (15-day lifetime), so those are not replayed.
static void AppendCookies(HttpContext http, IEnumerable<string> cookies) {
	foreach (var cookie in cookies)
		if (!System.Text.RegularExpressions.Regex.IsMatch(cookie, @"^[A-Za-z0-9]{40}=")) http.Response.Headers.Append("Set-Cookie", cookie);
}

static bool IsSlowUnderLoad(string path) =>
	path == "/api/auth/user"
	|| path.StartsWith("/api/v2/media/")
	|| (path.StartsWith("/api/v2/events/") && (path.EndsWith("/load-for-visitor") || path.EndsWith("/log-interaction")));

static string Key(string method, string host, string path, string query) => $"{method} {host}{path}{(query == "" ? "" : "?" + query)}";

// Everything the server answers from, built by BuildSnapshot with one set of random ids; a reset replaces it as a whole.
class Snapshot {
	public Snapshot(Dictionary<string, Sequence> sequences, List<string> hosts, List<Captured> entries, Captured comingSoon, Captured openTemplate, List<(string EventId, int JourneyId)> ids) {
		Sequences = sequences; Hosts = hosts; Entries = entries; ComingSoon = comingSoon; OpenTemplate = openTemplate; Ids = ids;
	}
	public List<(string EventId, int JourneyId)> Ids { get; }   // the current (event id, journey id) of each show
	public Dictionary<string, Sequence> Sequences { get; }
	public List<string> Hosts { get; }
	public List<Captured> Entries { get; }     // everything except the two show-list templates
	public Captured ComingSoon { get; }
	public Captured OpenTemplate { get; }
}

// A successful RSVP made here: the primary attendee first, then the plus-ones.
class Registration {
	public Registration(List<JsonObject> attendees, string eventId, int journeyId, int booking) { Attendees = attendees; EventId = eventId; JourneyId = journeyId; Booking = booking; }
	public List<JsonObject> Attendees { get; }
	public string EventId { get; }
	public int JourneyId { get; }
	public int Booking { get; }               // the primary attendee's booking number (registration_sequence_number)
	public bool JourneyLoaded { get; set; }   // load-for-visitor?attendee=ID has been asked for
	public bool EmailTriggered { get; set; }  // the log-interaction that triggers the confirmation email has arrived
}

class Show {
	public Show(string name, string eventId, int journeyId, DateTime start, DateTime end) { Name = name; EventId = eventId; JourneyId = journeyId; Start = start; End = end; }
	public DateTime Start { get; }     // event start / end as the page's JSON writes them (no time zone)
	public DateTime End { get; }
	public string Name { get; }
	public string EventId { get; }     // as captured
	public int JourneyId { get; }      // as captured
	public string NewEventId { get; set; } = "";
	public int NewJourneyId { get; set; }
	public Dictionary<int, int> IssuedIds { get; set; } = new();   // captured step / action id -> the id made up for this show
}

class Captured {
	public int Id { get; set; }
	public string Method { get; set; } = "";
	public string Host { get; set; } = "";
	public string Path { get; set; } = "";
	public string Query { get; set; } = "";
	public int Status { get; set; }
	public string ContentType { get; set; } = "";
	public List<string> Cookies { get; set; } = new();
	public string File { get; set; } = "";
	public string Role { get; set; } = "";
	public byte[] Body { get; set; } = Array.Empty<byte>();
	public Captured Copy() => (Captured)MemberwiseClone();
}

class Sequence {
	public List<Captured> Responses { get; } = new();
	int _served;
	// Under a lock so two overlapping requests never take the same next response.
	public (Captured Response, int Number, bool Reused) Next() {
		lock (this) {
			_served++;
			int n = Math.Min(_served, Responses.Count);
			return (Responses[n - 1], n, _served > Responses.Count);
		}
	}
}
