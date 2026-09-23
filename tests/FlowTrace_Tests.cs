namespace sws.Tests;

using System.Text.Json;
using Analysis;
using Saz;
using Shouldly;
using Xunit;
using static sws.Tests.TestExchangeBuilder;

public class FlowTrace_Tests {

	const string Host = "https://shop.example.com";
	const string SessionCookie = "abc123def456ghi789";

	static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

	static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) =>
		headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);

	static Exchange Submission(int id, params (string Name, string Value)[] cookies) => BuildExchange(
		id,
		"POST",
		$"{Host}/book",
		cookies: cookies.ToDictionary(c => c.Name, c => c.Value),
		requestHeaders: Headers(("User-Agent", "Mozilla/5.0"), ("Origin", Host)),
		requestJson: Json("""
			{ "firstName": "Chris", "lastName": "Rettig", "email": "chris@example.com", "groupSize": 2,
			  "eventId": 52549, "timezone": "America/New_York", "csrf": "9f8e7d6c5b4a39281706" }
			""")
	);

	static Exchange Index(int id) => BuildExchange(
		id,
		"GET",
		$"{Host}/index",
		responseHeaders: Headers(("Set-Cookie", $"SID={SessionCookie}; Path=/"), ("access-control-allow-origin", Host))
	);

	static Exchange Events(int id, string? sessionCookie = null) => BuildExchange(
		id,
		"GET",
		$"{Host}/api/events",
		cookies: sessionCookie is null ? null : new() { ["SID"] = sessionCookie },
		responseJson: Json("""[{ "id": 52549, "tz": "America/New_York" }]""")
	);

	static InputTrace Input(AnchorTrace trace, int exchangeId, string name) =>
		trace.Exchanges.Single(e => e.ExchangeId == exchangeId).Inputs.Single(i => i.Name == name);

	[Fact]
	public void SubmissionFinder_FindsFormPosts_AndFlagsTheLastAsLatest() {
		var exchanges = new List<Exchange> {
			Submission(1),
			BuildExchange(2, "POST", $"{Host}/analytics", requestJson: Json("""{ "path": "firstName", "action": "click", "sessionID": "x" }""")),
			BuildExchange(3, "GET", $"{Host}/book?firstName=a&lastName=b"),
			Submission(4),
		};

		var report = FlowTracer.TraceAll(exchanges, "capture.exchanges.json");

		report.Traces.Select(t => t.AnchorExchangeId).ShouldBe([1, 4]);
		report.Traces.Select(t => t.IsLatest).ShouldBe([false, true]);
	}

	[Fact]
	public void Trace_LabelsEachInputWithItsSource() {
		var exchanges = new List<Exchange> {
			Index(1),
			Events(2),
			Submission(3, ("SID", SessionCookie)),
		};

		var trace = FlowTracer.TraceAll(exchanges, "capture.exchanges.json").Traces.Single();

		var cookie = Input(trace, 3, "SID");
		cookie.Label.ShouldBe(InputLabel.Exchange);
		cookie.Source!.ExchangeId.ShouldBe(1);
		cookie.Source.Where.ShouldBe("Set-Cookie: SID");

		var eventId = Input(trace, 3, "$.eventId");
		eventId.Label.ShouldBe(InputLabel.Exchange);
		eventId.Source!.ExchangeId.ShouldBe(2);
		eventId.Source.Where.ShouldBe("response JSON: $[0].id");

		Input(trace, 3, "$.timezone").Source!.Where.ShouldBe("response JSON: $[0].tz");

		var firstName = Input(trace, 3, "$.firstName");
		firstName.Label.ShouldBe(InputLabel.PreKnownConstant);
		firstName.Note.ShouldBe("user-supplied");

		Input(trace, 3, "$.csrf").Label.ShouldBe(InputLabel.Unknown);
		Input(trace, 3, "User-Agent").Label.ShouldBe(InputLabel.BrowserDefault);
		Input(trace, 3, "path[0]").Label.ShouldBe(InputLabel.PreKnownConstant);
	}

	[Fact]
	public void Trace_OriginHeader_IsNotSourcedFromACorsResponseHeader() {
		var exchanges = new List<Exchange> { Index(1), Submission(2) };

		var trace = FlowTracer.TraceAll(exchanges, "capture.exchanges.json").Traces.Single();

		var origin = Input(trace, 2, "Origin");
		origin.Label.ShouldBe(InputLabel.PreKnownConstant);
		origin.Source.ShouldBeNull();
	}

	[Fact]
	public void Trace_EarliestSupplierWins_AndLaterOnesAreListed() {
		var exchanges = new List<Exchange> { Events(1), Events(2), Events(3), Submission(4) };

		var trace = FlowTracer.TraceAll(exchanges, "capture.exchanges.json").Traces.Single();

		var eventId = Input(trace, 4, "$.eventId");
		eventId.Source!.ExchangeId.ShouldBe(1);
		eventId.AlsoSuppliedBy.ShouldBe([2, 3]);
	}

	[Fact]
	public void Trace_OnlyExchangesBeforeTheConsumerCanSupply() {
		var exchanges = new List<Exchange> { Submission(1), Events(2) };

		var trace = FlowTracer.TraceAll(exchanges, "capture.exchanges.json").Traces.Single();

		Input(trace, 1, "$.eventId").Source.ShouldBeNull();
	}

	[Fact]
	public void Trace_MarksAnchorSourcesAndBystandersAsParticipatingOrNot() {
		var exchanges = new List<Exchange> {
			Index(1),
			BuildExchange(2, "GET", $"{Host}/unrelated", responseJson: Json("""{ "nothing": "of interest" }""")),
			Events(3),
			Submission(4, ("SID", SessionCookie)),
		};

		var trace = FlowTracer.TraceAll(exchanges, "capture.exchanges.json").Traces.Single();

		trace.Exchanges.Select(e => e.Role).ShouldBe(["source", "none", "source", "anchor"]);
		trace.Exchanges.Select(e => e.Participates).ShouldBe([true, false, true, true]);
		trace.Exchanges.Single(e => e.ExchangeId == 2).Inputs.ShouldBeEmpty();
	}

	[Fact]
	public void Trace_FollowsSourcesBackToTheirOwnSources() {
		// The anchor only needs the event id, but the events request itself needed a session cookie from the index page.
		var exchanges = new List<Exchange> {
			Index(1),
			Events(2, sessionCookie: SessionCookie),
			Submission(3),
		};

		var trace = FlowTracer.TraceAll(exchanges, "capture.exchanges.json").Traces.Single();

		trace.Exchanges.Select(e => e.Role).ShouldBe(["source", "source", "anchor"]);
		Input(trace, 2, "SID").Source!.ExchangeId.ShouldBe(1);
	}

	[Fact]
	public void Trace_RecordsWhichResponseValuesWereUsedLater() {
		var exchanges = new List<Exchange> { Events(1), Submission(2) };

		var trace = FlowTracer.TraceAll(exchanges, "capture.exchanges.json").Traces.Single();

		var output = trace.Exchanges.Single(e => e.ExchangeId == 1).Outputs.Single(o => o.Where == "response JSON: $[0].id");
		output.Value.ShouldBe("52549");
		output.ConsumedBy.ShouldBe(["2 JsonBodyField $.eventId"]);
	}

	[Fact]
	public void ToMarkdown_ListsParticipantsAndGroupsTheRest() {
		var exchanges = new List<Exchange> {
			BuildExchange(1, "GET", $"{Host}/static/a.html"),
			BuildExchange(2, "GET", $"{Host}/static/a.html"),
			Events(3),
			Submission(4),
		};

		string markdown = FlowTraceWriter.ToMarkdown(FlowTracer.TraceAll(exchanges, "capture.exchanges.json"));

		markdown.ShouldContain("Form submissions found: 4 (latest: 4).");
		markdown.ShouldContain("Participating exchanges (2): 3, 4");
		markdown.ShouldContain("| GET shop.example.com/static/a.html | 2 | 1, 2 |");
	}
}
