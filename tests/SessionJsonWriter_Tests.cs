namespace sws.Tests;

using System.Text.Json;
using Saz;
using SazJson;
using Shouldly;
using Xunit;
using static sws.Tests.TestExchangeBuilder;

public class SessionJsonWriter_Tests {

	static Session BuildSession(params Exchange[] exchanges) => new("capture.saz", [.. exchanges]);

	static Exchange WithHeaders(Exchange exchange, params (string Name, string Value)[] headers) {
		var dictionary = headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);
		return exchange with { Request = exchange.Request with { Headers = dictionary } };
	}

	[Fact]
	public void ToJson_MovesHeadersSharedByEveryExchangeIntoGlobalHeaders() {
		var session = BuildSession(
			WithHeaders(BuildExchange(1, "GET", "https://example.com/a"), ("User-Agent", "sws"), ("X-Only-A", "1")),
			WithHeaders(BuildExchange(2, "GET", "https://example.com/b"), ("User-Agent", "sws"))
		);

		using var json = JsonDocument.Parse(SessionJsonWriter.ToJson(session, new SessionJsonOptions()));

		var root = json.RootElement;
		root.GetProperty("GlobalHeaders").GetProperty("Headers").GetProperty("User-Agent").GetString().ShouldBe("sws");
		var first = root.GetProperty("Exchanges")[0].GetProperty("Request").GetProperty("Headers");
		first.TryGetProperty("User-Agent", out _).ShouldBeFalse();
		first.GetProperty("X-Only-A").GetString().ShouldBe("1");
	}

	[Fact]
	public void ToJson_UsesExchangeIdKey() {
		var session = BuildSession(BuildExchange(7, "GET", "https://example.com/a"));

		using var json = JsonDocument.Parse(SessionJsonWriter.ToJson(session, new SessionJsonOptions()));

		json.RootElement.GetProperty("Exchanges")[0].GetProperty("ExchangeId").GetInt32().ShouldBe(7);
	}

	[Fact]
	public void ToJson_OmitsMetadata_UnlessRequested() {
		var metadata = new Metadata(new() { ["flag"] = "1" }, new());
		var session = BuildSession(BuildExchange(1, "GET", "https://example.com/a") with { Metadata = metadata });

		using var without = JsonDocument.Parse(SessionJsonWriter.ToJson(session, new SessionJsonOptions()));
		using var with = JsonDocument.Parse(SessionJsonWriter.ToJson(session, new SessionJsonOptions { IncludeMetadata = true }));

		without.RootElement.GetProperty("Exchanges")[0].TryGetProperty("Metadata", out _).ShouldBeFalse();
		with.RootElement.GetProperty("Exchanges")[0].GetProperty("Metadata").GetProperty("Flags").GetProperty("flag").GetString().ShouldBe("1");
	}
}
