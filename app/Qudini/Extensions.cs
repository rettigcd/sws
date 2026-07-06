namespace Qudini;

static internal class Extensions {

	static internal async Task<StepResult> Bob(this Func<Context,Task<StepResult>> action, Context context) {
		return await action(context);
	}

	static async Task<HttpResponseMessage> SendHedgedAsync(
		this Func<Context, CancellationToken, Task<StepResult>> send,
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


// public sealed class RetryingStep<TContext> : IAuthStep<TContext> {

// 	readonly IAuthStep<TContext> inner;
// 	readonly RetryPolicy policy;
// 	readonly ILogger logger;

// 	public string Name => inner.Name;

// 	public RetryingStep( IAuthStep<TContext> inner, RetryPolicy policy, ILogger logger) {
// 		this.inner = inner;
// 		this.policy = policy;
// 		this.logger = logger;
// 	}

// 	public async Task<StepResult> ExecuteAsync( TContext context, CancellationToken cancellationToken) {
// 		for (int attempt = 1; attempt <= policy.MaxAttempts; attempt++) {
// 			using var attemptCts =
// 				CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

// 			attemptCts.CancelAfter(policy.AttemptTimeout);

// 			try {
// 				logger.LogInformation("Executing step {StepName}, attempt {Attempt}/{MaxAttempts}",Name,attempt,policy.MaxAttempts);

// 				StepResult result = await inner.ExecuteAsync(context, attemptCts.Token);

// 				if (result.IsSuccess)
// 					return result;

// 				logger.LogWarning("Step {StepName}, attempt {Attempt} failed: {Reason}",Name,attempt,result.ErrorMessage);

// 				if (!result.IsRetriable)
// 					return result;
// 			}
// 			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
// 				logger.LogWarning("Step {StepName}, attempt {Attempt} timed out after {Timeout}",Name,attempt,policy.AttemptTimeout);
// 			}
// 			catch (Exception ex) {
// 				logger.LogWarning(ex,"Step {StepName}, attempt {Attempt} threw an exception",Name,attempt);
// 			}

// 			if (attempt < policy.MaxAttempts && policy.DelayBetweenAttempts > TimeSpan.Zero)
// 				await Task.Delay(policy.DelayBetweenAttempts, cancellationToken);
// 		}

// 		return StepResult.Failed($"{Name} failed after {policy.MaxAttempts} attempts.",isRetriable: false);
// 	}
// }
