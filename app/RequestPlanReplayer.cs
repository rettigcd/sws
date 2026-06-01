internal static class RequestPlanReplayer {
	public static async Task<List<HttpResponseMessage>> ExecuteSequentially(
		IReadOnlyList<RequestPlan> plans,
		RequestExecutionContext? context = null,
		bool seedCapturedCookies = true,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull(plans);

		var ownsContext = context is null;
		context ??= new RequestExecutionContext();

		try {
			var responses = new List<HttpResponseMessage>(plans.Count);

			foreach (var plan in plans) {
				cancellationToken.ThrowIfCancellationRequested();
				responses.Add(await plan.Execute(context, seedCapturedCookies, cancellationToken).ConfigureAwait(false));
			}

			return responses;
		}
		finally {
			if (ownsContext)
				context.Dispose();
		}
	}

	public static Task<List<HttpResponseMessage>> ExecuteSessionsSequentially(
		IReadOnlyList<Session> sessions,
		RequestExecutionContext? context = null,
		bool seedCapturedCookies = true,
		CancellationToken cancellationToken = default
	) {
		ArgumentNullException.ThrowIfNull(sessions);

		var plans = sessions
			.Select(session => new RequestPlan(session.Request))
			.ToList();

		return ExecuteSequentially(plans, context, seedCapturedCookies, cancellationToken);
	}
}