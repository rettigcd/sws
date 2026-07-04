using System.Net;

namespace Automation;

internal static class AuthorizationCodeFlowHandler {

	static readonly string[] PreservedConfigurationParamNames = ["p", "response_mode", "prompt", "login_hint", "domain_hint", "ui_locales"];
	static readonly string[] RegeneratedCorrelationParamNames = ["client-request-id", "correlation_id", "request_id"];

	public static async Task<AutomationResult> ExecuteAsync(
		Auth.DetectedAuthenticationFlow flow,
		IReadOnlyList<Session> sessions,
		AutomationOptions options,
		IAuthHttpClient httpClient,
		CancellationToken cancellationToken
	) {
		var stepLog = new AutomationStepLog();
		var variables = new List<ResolvedVariable>();

		var endpoints = EndpointResolver.Resolve(flow, sessions);
		if (string.IsNullOrWhiteSpace(endpoints.AuthorizationEndpoint) || string.IsNullOrWhiteSpace(endpoints.TokenEndpoint)) {
			stepLog.Record("Unable to resolve authorization/token endpoints for this flow.", success: false);
			return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(UnsupportedFlowReasonKind.MissingRequiredEndpoint, "Could not resolve authorization/token endpoints from discovery, B2C details, or captured sessions."));
		}

		if (string.IsNullOrWhiteSpace(flow.ClientId))
			return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(UnsupportedFlowReasonKind.MissingCredentials, "Flow has no client_id."));

		string? redirectUri = options.RedirectUriOverride ?? flow.RedirectUri;
		if (string.IsNullOrWhiteSpace(redirectUri))
			return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(UnsupportedFlowReasonKind.MissingRequiredEndpoint, "Flow has no redirect_uri."));

		string clientId = flow.ClientId;
		var scopes = options.ScopesOverride ?? flow.Scopes;
		bool isPkce = flow.FlowType == Auth.AuthFlowType.AuthorizationCodeWithPkce;

		string state = OAuthCryptoHelpers.GenerateState();
		variables.Add(new ResolvedVariable("state", state, VariableProvenance.Generated));

		string? nonce = null;
		if (flow.Variables.Any(v => v.Name.Equals("nonce", StringComparison.OrdinalIgnoreCase))) {
			nonce = OAuthCryptoHelpers.GenerateNonce();
			variables.Add(new ResolvedVariable("nonce", nonce, VariableProvenance.Generated));
		}

		string? codeVerifier = null;
		string? codeChallenge = null;
		string codeChallengeMethod = "S256";
		if (isPkce) {
			codeVerifier = OAuthCryptoHelpers.GenerateCodeVerifier();
			string? capturedMethod = flow.Variables.FirstOrDefault(v => v.Name.Equals("code_challenge_method", StringComparison.OrdinalIgnoreCase))?.Value;
			if (!string.IsNullOrWhiteSpace(capturedMethod))
				codeChallengeMethod = capturedMethod;

			codeChallenge = codeChallengeMethod.Equals("S256", StringComparison.OrdinalIgnoreCase)
				? OAuthCryptoHelpers.DeriveCodeChallengeS256(codeVerifier)
				: codeVerifier;

			variables.Add(new ResolvedVariable("code_verifier", codeVerifier, VariableProvenance.Generated));
			variables.Add(new ResolvedVariable("code_challenge", codeChallenge, VariableProvenance.Generated, DerivedFromVariableName: "code_verifier"));
		}

		var authorizeQuery = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
			["client_id"] = clientId,
			["response_type"] = flow.Variables.FirstOrDefault(v => v.Name.Equals("response_type", StringComparison.OrdinalIgnoreCase))?.Value ?? "code",
			["redirect_uri"] = redirectUri,
			["state"] = state,
		};
		if (scopes.Count > 0)
			authorizeQuery["scope"] = string.Join(' ', scopes);
		if (nonce is not null)
			authorizeQuery["nonce"] = nonce;
		if (isPkce) {
			authorizeQuery["code_challenge"] = codeChallenge!;
			authorizeQuery["code_challenge_method"] = codeChallengeMethod;
		}

		foreach (string name in PreservedConfigurationParamNames) {
			var captured = flow.Variables.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && v.Category == Auth.VariableCategory.Configuration);
			if (captured is null)
				continue;

			authorizeQuery[name] = captured.Value;
			variables.Add(new ResolvedVariable(name, captured.Value, VariableProvenance.Discovered, captured.Category));
		}

		foreach (string name in RegeneratedCorrelationParamNames) {
			if (!flow.Variables.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
				continue;

			string fresh = Guid.NewGuid().ToString();
			authorizeQuery[name] = fresh;
			variables.Add(new ResolvedVariable(name, fresh, VariableProvenance.Generated));
		}

		variables.Add(new ResolvedVariable("client_id", clientId, VariableProvenance.Discovered, Auth.VariableCategory.Configuration));
		variables.Add(new ResolvedVariable("redirect_uri", redirectUri, options.RedirectUriOverride is not null ? VariableProvenance.CallerSupplied : VariableProvenance.Discovered, Auth.VariableCategory.Configuration));

		var authorizeUri = BuildUri(endpoints.AuthorizationEndpoint!, authorizeQuery);
		bool useFragmentCallback = string.Equals(flow.B2cDetails?.ResponseMode, "fragment", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(authorizeQuery.GetValueOrDefault("response_mode"), "fragment", StringComparison.OrdinalIgnoreCase);

		var currentRequest = BuildGetRequest(authorizeUri);
		stepLog.Record("Sent authorization request to authorize endpoint.", requestUrl: authorizeUri.ToString());
		var response = await httpClient.SendAsync(currentRequest, cancellationToken).ConfigureAwait(false);

		string? code = null;
		int redirectHops = 0;
		int loginHops = 0;

		while (true) {
			cancellationToken.ThrowIfCancellationRequested();

			if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null) {
				var location = response.Headers.Location.IsAbsoluteUri
					? response.Headers.Location
					: new Uri(currentRequest.RequestUri!, response.Headers.Location);

				if (RedirectFollower.MatchesRedirectUri(location, redirectUri)) {
					var callbackParams = RedirectFollower.ExtractCallbackParameters(location, useFragmentCallback);
					stepLog.Record("Captured authorization result from redirect to redirect_uri.", httpStatusCode: (int)response.StatusCode, requestUrl: location.ToString());

					if (callbackParams.TryGetValue("error", out string? errorCode)) {
						string? errorDescription = callbackParams.GetValueOrDefault("error_description", errorCode);
						return Failure(flow, stepLog, variables, httpClient, errorMessage: $"Authorization server returned error: {errorDescription}");
					}

					if (!callbackParams.TryGetValue("code", out code) || string.IsNullOrWhiteSpace(code))
						return Failure(flow, stepLog, variables, httpClient, errorMessage: "Redirect to redirect_uri did not include an authorization code.");

					variables.Add(new ResolvedVariable("code", code, VariableProvenance.Extracted, Auth.VariableCategory.ServerGenerated));

					if (callbackParams.TryGetValue("state", out string? returnedState) && !string.Equals(returnedState, state, StringComparison.Ordinal)) {
						stepLog.Record("Returned state did not match the state sent on the authorization request.", success: false);
						return Failure(flow, stepLog, variables, httpClient, errorMessage: "Returned state did not match the state sent on the authorization request.");
					}

					break;
				}

				redirectHops++;
				if (redirectHops > options.MaxRedirects)
					return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(UnsupportedFlowReasonKind.Other, "Exceeded maximum redirect hop count without reaching redirect_uri."));

				stepLog.Record("Followed IdP-internal redirect.", httpStatusCode: (int)response.StatusCode, requestUrl: location.ToString());
				currentRequest = BuildGetRequest(location);
				response = await httpClient.SendAsync(currentRequest, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (response.StatusCode == HttpStatusCode.OK && IsHtmlResponse(response)) {
				if (flow.AuthenticationMethod is not Auth.UsernamePasswordCredentials capturedCredentials) {
					return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(
						UnsupportedFlowReasonKind.MissingCredentials,
						"Session cookies/SSO were not sufficient to complete authentication this run, and the original flow did not use a username/password login form."
					));
				}

				loginHops++;
				if (loginHops > options.MaxLoginPageHops)
					return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(UnsupportedFlowReasonKind.Other, "Exceeded maximum login-page hop count."));

				string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				string pageUrl = currentRequest.RequestUri!.ToString();
				var parseResult = await LoginPageParser.ParseAsync(html, pageUrl).ConfigureAwait(false);

				if (parseResult.Outcome != LoginPageParseOutcome.LoginForm) {
					var reasonKind = parseResult.Outcome switch {
						LoginPageParseOutcome.CaptchaRequired => UnsupportedFlowReasonKind.CaptchaRequired,
						LoginPageParseOutcome.WebAuthnRequired => UnsupportedFlowReasonKind.WebAuthnOrFido2Required,
						LoginPageParseOutcome.JavaScriptRequired => UnsupportedFlowReasonKind.JavaScriptRequired,
						LoginPageParseOutcome.MfaRequired => UnsupportedFlowReasonKind.MfaRequired,
						_ => UnsupportedFlowReasonKind.Other,
					};
					return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(reasonKind, parseResult.Detail ?? "Login page could not be automated."));
				}

				if (loginHops > 1)
					return Failure(flow, stepLog, variables, httpClient, errorMessage: "Login form was re-displayed after submission; credentials were likely rejected.");

				var form = parseResult.Form!;
				string username = options.UsernameOverride ?? capturedCredentials.Username;
				string password = options.PasswordOverride ?? capturedCredentials.Password;
				variables.Add(new ResolvedVariable(
					"username", username,
					options.UsernameOverride is not null ? VariableProvenance.CallerSupplied : VariableProvenance.Discovered,
					Notes: options.UsernameOverride is null ? "Fallback to originally-captured username; may be stale/test-only." : null
				));
				variables.Add(new ResolvedVariable(
					"password", password,
					options.PasswordOverride is not null ? VariableProvenance.CallerSupplied : VariableProvenance.Discovered,
					Notes: options.PasswordOverride is null ? "Fallback to originally-captured password; may be stale/test-only." : null
				));

				var formFields = new Dictionary<string, string>(StringComparer.Ordinal);
				foreach (var hidden in form.HiddenFields)
					formFields[hidden.Name] = hidden.Value;
				if (form.UsernameFieldName is not null)
					formFields[form.UsernameFieldName] = username;
				formFields[form.PasswordFieldName] = password;

				var postRequest = new HttpRequestMessage(new HttpMethod(form.Method), form.ActionUrl) {
					Content = new FormUrlEncodedContent(formFields),
				};
				HeaderHelpers.Apply(postRequest);

				string hiddenFieldNames = form.HiddenFields.Count > 0 ? $" incl. {string.Join(", ", form.HiddenFields.Select(f => $"'{f.Name}'"))}" : string.Empty;
				stepLog.Record($"Submitted login form ({form.HiddenFields.Count} hidden field(s){hiddenFieldNames}).", requestUrl: form.ActionUrl);

				currentRequest = postRequest;
				response = await httpClient.SendAsync(currentRequest, cancellationToken).ConfigureAwait(false);
				continue;
			}

			return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(
				UnsupportedFlowReasonKind.Other,
				$"Unexpected response (status {(int)response.StatusCode}) while following the authorization flow.",
				[$"status:{(int)response.StatusCode}"]
			));
		}

		var tokenFields = new Dictionary<string, string> {
			["grant_type"] = "authorization_code",
			["code"] = code!,
			["redirect_uri"] = redirectUri,
			["client_id"] = clientId,
		};
		if (isPkce)
			tokenFields["code_verifier"] = codeVerifier!;

		bool requiresSecret = flow.ReplayRequirements.Any(r => r.Kind == Auth.ReplayRequirementKind.RequireClientSecret);
		if (requiresSecret) {
			string? clientSecret = options.ClientSecretOverride
				?? flow.Variables.FirstOrDefault(v => v.Category == Auth.VariableCategory.Secret && v.Name.Equals("client_secret", StringComparison.OrdinalIgnoreCase))?.Value;

			if (string.IsNullOrWhiteSpace(clientSecret))
				return Failure(flow, stepLog, variables, httpClient, new UnsupportedFlowReason(UnsupportedFlowReasonKind.MissingCredentials, "Token endpoint requires a client_secret, but none was available."));

			tokenFields["client_secret"] = clientSecret;
			variables.Add(new ResolvedVariable("client_secret", clientSecret, options.ClientSecretOverride is not null ? VariableProvenance.CallerSupplied : VariableProvenance.Discovered, Auth.VariableCategory.Secret));
		}

		var tokenRequest = new HttpRequestMessage(HttpMethod.Post, endpoints.TokenEndpoint) {
			Content = new FormUrlEncodedContent(tokenFields),
		};
		HeaderHelpers.Apply(tokenRequest);

		stepLog.Record("Exchanged authorization code for tokens.", requestUrl: endpoints.TokenEndpoint);
		var tokenResponse = await httpClient.SendAsync(tokenRequest, cancellationToken).ConfigureAwait(false);

		return await TokenExchange.ParseResponseAsync(flow, stepLog, variables, httpClient, tokenResponse, cancellationToken).ConfigureAwait(false);
	}

	static HttpRequestMessage BuildGetRequest(Uri uri) {
		var request = new HttpRequestMessage(HttpMethod.Get, uri);
		HeaderHelpers.Apply(request);
		return request;
	}

	static Uri BuildUri(string baseUrl, Dictionary<string, string> query) {
		var pairs = query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
		string separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
		return new Uri($"{baseUrl}{separator}{string.Join("&", pairs)}");
	}

	static bool IsHtmlResponse(HttpResponseMessage response) {
		return response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
	}

	static AutomationResult Failure(
		Auth.DetectedAuthenticationFlow flow,
		AutomationStepLog stepLog,
		List<ResolvedVariable> variables,
		IAuthHttpClient httpClient,
		UnsupportedFlowReason? reason = null,
		string? errorMessage = null
	) {
		return new AutomationResult(false, flow.FlowId, flow.FlowType, null, TokenExchange.ExtractCookies(httpClient), [], stepLog.ToList(), variables, reason, errorMessage);
	}
}
