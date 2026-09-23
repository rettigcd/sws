namespace sws.Tests;

using Analysis;
using Saz;
using Shouldly;
using Xunit;
using static sws.Tests.TestExchangeBuilder;

public class ExchangeFilter_Tests {

	static readonly Exchange[] Exchanges = [
		BuildExchange(1, "GET", "https://example.com/api"),
		BuildExchange(2, "GET", "https://example.com/site.css"),
		BuildExchange(3, "GET", "https://example.com/logo.png"),
		BuildExchange(4, "GET", "https://example.com/app.js.map"),
		BuildExchange(5, "CONNECT", "https://example.com:443"),
		BuildExchange(6, "GET", "https://svcs.tql.com/api"),
	];

	[Fact]
	public void Apply_DropsCssMediaSourcemapsConnectAndExcludedHosts_ByDefault() {
		var kept = ExchangeFilter.Apply(Exchanges, new ExchangeFilterOptions());

		kept.Select(e => e.ExchangeId).ShouldBe([1]);
	}

	[Fact]
	public void Apply_KeepsCategoriesThatAreIncluded() {
		var kept = ExchangeFilter.Apply(Exchanges, new ExchangeFilterOptions {
			IncludeConnect = true,
			IncludeCss = true,
			IncludeMedia = true,
			IncludeSourcemaps = true,
		});

		kept.Select(e => e.ExchangeId).ShouldBe([1, 2, 3, 4, 5]);
	}
}
