namespace sws.Tests;

using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Automation;
using Shouldly;
using Xunit;

/// <summary>
/// Exercises the real .NET HTTP stack (not FakeAuthHttpClient) against a raw TCP server, to prove
/// SystemNetAuthHttpClient's HttpClientHandler correctly captures every Set-Cookie header from a
/// response - not just the last one - even when that response body is Brotli-compressed.
/// </summary>
public class SystemNetAuthHttpClient_Tests {

	[Fact]
	public async Task SendAsync_CapturesEveryMultiLineSetCookieHeader_WhileDecompressingBrotliBody() {
		const string expectedBody = "hello from a brotli-compressed, multi-cookie response";
		var compressedBody = CompressBrotli(expectedBody);

		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;

		var serverTask = Task.Run(async () => {
			using var connection = await listener.AcceptTcpClientAsync();
			using var stream = connection.GetStream();

			var buffer = new byte[4096];
			_ = await stream.ReadAsync(buffer);

			var responseHeader = string.Join("\r\n", [
				"HTTP/1.1 200 OK",
				"Content-Type: text/plain; charset=utf-8",
				"Content-Encoding: br",
				"Set-Cookie: first=1; Path=/",
				"Set-Cookie: second=2; Path=/",
				"Set-Cookie: third=3; Path=/",
				$"Content-Length: {compressedBody.Length}",
				"Connection: close",
				"",
				"",
			]);

			var headerBytes = Encoding.ASCII.GetBytes(responseHeader);
			await stream.WriteAsync(headerBytes);
			await stream.WriteAsync(compressedBody);
		});

		using var client = new SystemNetAuthHttpClient();
		var requestUri = new Uri($"http://127.0.0.1:{port}/");

		var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, requestUri));
		var body = await response.Content.ReadAsStringAsync();
		await serverTask;
		listener.Stop();

		body.ShouldBe(expectedBody);

		var cookies = client.Cookies.GetCookies(requestUri);
		cookies.Count.ShouldBe(3);
		cookies["first"]!.Value.ShouldBe("1");
		cookies["second"]!.Value.ShouldBe("2");
		cookies["third"]!.Value.ShouldBe("3");
	}

	static byte[] CompressBrotli(string text) {
		using var output = new MemoryStream();
		using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
			brotli.Write(Encoding.UTF8.GetBytes(text));
		return output.ToArray();
	}
}
