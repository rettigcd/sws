namespace Qudini;

class Extensions {

	async Task<HttpResponseMessage> SendHedgedAsync(
		Func<CancellationToken, Task<HttpResponseMessage>> send,
		TimeSpan hedgeDelay,
		int maxAttempts,
		CancellationToken outerToken
	) {
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);

		var tasks = new List<Task<HttpResponseMessage>>();

		for (int i = 0; i < maxAttempts; i++) {
			tasks.Add(send(cts.Token));

			if (i < maxAttempts - 1)
				await Task.Delay(hedgeDelay, outerToken);
		}

		while (tasks.Count > 0) {
			var completed = await Task.WhenAny(tasks);
			tasks.Remove(completed);

			try {
				var response = await completed;

				if ((int)response.StatusCode < 500) {
					cts.Cancel(); // stop the losing attempts
					return response;
				}

				response.Dispose(); // 500/520/etc: keep waiting for another attempt
			}
			catch when (!outerToken.IsCancellationRequested) {
				// ignore failed attempt; wait for another
			}
		}

		throw new HttpRequestException("All hedged attempts failed.");
	}
}