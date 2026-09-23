namespace sws.Tests;

using System.IO.Compression;
using System.Text;
using Saz;
using Shouldly;
using Xunit;

public class SazReader_Tests {

	[Fact]
	public void Read_ReturnsEveryExchangeInIdOrder_WithoutFiltering() {
		using var saz = TempSaz.Create(
			(2, "GET /style.css HTTP/1.1\r\nHost: example.com\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Type: text/css\r\n\r\nbody{}", null),
			(1, "CONNECT example.com:443 HTTP/1.1\r\n\r\n", "HTTP/1.1 200 Connection Established\r\n\r\n", null)
		);

		var session = SazReader.Read(saz.Path);

		session.Exchanges.Select(e => e.ExchangeId).ShouldBe([1, 2]);
		session.Exchanges[0].Request.Method.ShouldBe("CONNECT");
		session.Exchanges[1].Request.Url.ShouldBe("http://example.com/style.css");
	}

	[Fact]
	public void Read_KeepsAllHeaders_IncludingPerRequestOnes() {
		using var saz = TempSaz.Create(
			(1, "GET /a HTTP/1.1\r\nHost: example.com\r\nContent-Length: 0\r\nDate: today\r\nX-Request-Id: abc\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n", null)
		);

		var request = SazReader.Read(saz.Path).Exchanges.Single().Request;

		request.Headers.Keys.ShouldBe(["Content-Length", "Date", "Host", "X-Request-Id"]);
	}

	[Fact]
	public void Read_ParsesUrlQueryCookiesAndJsonBodies() {
		using var saz = TempSaz.Create(
			(1,
			 "POST /api?x=1&y=two HTTP/1.1\r\nHost: example.com\r\nCookie: a=1; b=2\r\nContent-Type: application/json\r\n\r\n{\"k\":\"v\"}",
			 "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"ok\":true}",
			 null)
		);

		var exchange = SazReader.Read(saz.Path).Exchanges.Single();

		exchange.Request.Url.ShouldBe("http://example.com/api?x=1&y=two");
		exchange.Request.QueryParameters["y"].ShouldBe("two");
		exchange.Request.Cookies["b"].ShouldBe("2");
		exchange.Request.JsonBody!.Value.GetProperty("k").GetString().ShouldBe("v");
		exchange.Response.StatusCode.ShouldBe(200);
		exchange.Response.ResponseJson!.Value.GetProperty("ok").GetBoolean().ShouldBeTrue();
	}

	[Fact]
	public void Read_ParsesMetadataAndTimestamp() {
		const string metadata = """
			<Session>
				<SessionTimers ClientBeginRequest="2026-05-14T10:00:00.0000000-04:00" />
				<SessionFlags><SessionFlag N="x-overrideGateway" V="https" /></SessionFlags>
			</Session>
			""";
		using var saz = TempSaz.Create(
			(1, "GET /a HTTP/1.1\r\nHost: example.com\r\n\r\n", "HTTP/1.1 200 OK\r\n\r\n", metadata)
		);

		var exchange = SazReader.Read(saz.Path).Exchanges.Single();

		exchange.Metadata!.Flags["x-overrideGateway"].ShouldBe("https");
		exchange.Timestamp.ShouldBe(DateTimeOffset.Parse("2026-05-14T10:00:00-04:00"));
	}

	sealed class TempSaz : IDisposable {
		public string Path { get; }

		TempSaz(string path) { Path = path; }

		public static TempSaz Create(params (int Id, string Request, string Response, string? Metadata)[] exchanges) {
			string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sws-test-{Guid.NewGuid():N}.saz");
			using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) {
				foreach (var exchange in exchanges) {
					Add(archive, $"raw/{exchange.Id}_c.txt", exchange.Request);
					Add(archive, $"raw/{exchange.Id}_s.txt", exchange.Response);
					if (exchange.Metadata is not null)
						Add(archive, $"raw/{exchange.Id}_m.xml", exchange.Metadata);
				}
			}

			return new TempSaz(path);
		}

		static void Add(ZipArchive archive, string name, string content) {
			using var stream = archive.CreateEntry(name).Open();
			stream.Write(Encoding.Latin1.GetBytes(content));
		}

		public void Dispose() => File.Delete(Path);
	}
}
