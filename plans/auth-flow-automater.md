# Authentication Flow Automation Engine

## Context

`docs/AUTH_FLOW_AUTOMATER_SPEC.md` describes the "replay engine" (component 2) that was explicitly deferred when we built the OIDC/OAuth2/B2C detector (`app/Auth/AuthFlowDetector.cs`, component 1). It consumes a `Auth.DetectedAuthenticationFlow` and actually executes the flow against a real or mocked HTTP endpoint to obtain live tokens/cookies — protocol-aware, not byte-for-byte replay of captured `.saz` sessions (that's the unrelated `RequestPlan`/`RequestPlanReplayer` machinery, not reused here).

Decisions confirmed with the user:
- **v1 grant scope**: only `AuthorizationCode` / `AuthorizationCodeWithPkce` (+ optional login-form step) and `RefreshToken`. Every other `Auth.AuthFlowType` returns a structured `UnsupportedFlowReason` immediately, with zero HTTP calls — no partial execution, no throw.
- **New dependency**: add `AngleSharp` to `app/sws.csproj` for HTML parsing (hidden inputs, anti-forgery tokens, form action resolution) — the library already named in `todo.txt`'s HTML-parsing notes.
- **No runtime environment guardrail**: the engine will hit whatever host the flow points at, including production. The *only* safety mechanism is that tests exclusively use a fake `IAuthHttpClient` — never the real network implementation.
- **Redaction**: `AutomationResult.Variables` (the provenance trace) stays unredacted, consistent with the detector's existing "display everything" precedent. `Tokens`/`Cookies` were always unredacted (that's the point of the engine). Only the human-readable `Steps` log avoids embedding raw secret/token *values* (names/roles/counts only) — this is what satisfies the automation spec's "never store secrets in logs" wording without contradicting "display everything" for the structured result itself.
- **Scope**: library-only this pass. No CLI wiring in `app/Program.cs` — the spec's success criteria only describe engine behavior, not a CLI surface. Wiring a live-network command that submits real credentials is a separate, more consequential decision for later.

## Key design decisions

1. **`redirect_uri` is captured from the `Location` header, never fetched.** The engine follows IdP-internal redirect hops live, but the moment a `Location` matches the flow's `redirect_uri` (scheme+host+path), it parses `code`/`state` straight out of that URL string and stops. `redirect_uri` is the *client app's* endpoint (often `localhost:<port>` or a custom scheme) — actually requesting it is out of scope ("not a browser emulator") and unnecessary.
2. **`ReplayRequirementKind.RequireInteractiveLogin` is not a bail-out signal** — it's attached to essentially every AuthorizationCode/PKCE flow by the detector and is exactly the login-form step this engine automates. Only concrete *runtime* evidence in the actual HTML response (CAPTCHA/WebAuthn/JS-shell markers, extra required MFA-shaped fields) triggers `UnsupportedFlowReason`.
3. **Provenance vocabulary** follows the spec's own wording verbatim: `Generated` / `Discovered` / `Extracted` / `CallerSupplied`. Falling back to a stale captured value (e.g. an old refresh_token) is modeled as `Discovered` + a `Notes` string explaining the staleness caveat.

## Folder / namespace layout

New folder `app/Automation/`, namespace `Automation`, all `internal` (tests project already has `InternalsVisibleTo`).

| File | Purpose |
|---|---|
| `IAuthHttpClient.cs` | Mockable HTTP abstraction (interface, using `HttpRequestMessage`/`HttpResponseMessage` directly) + `SystemNetAuthHttpClient` production impl (`AllowAutoRedirect=false`, engine owns redirects) |
| `OAuthCryptoHelpers.cs` | `GenerateState`, `GenerateNonce`, `GenerateCodeVerifier`, `DeriveCodeChallengeS256`, `Base64UrlEncode` |
| `JwtDecoder.cs` | Unsigned, no-verification JWT payload → claims decoding |
| `EndpointResolver.cs` | Resolves literal authorize/token endpoint URLs: `Discovery` → `B2cDetails` → captured `Session` lookup by ID → null |
| `VariableProvenance.cs` | `VariableProvenance` enum + `ResolvedVariable` record |
| `UnsupportedFlowReason.cs` | `UnsupportedFlowReasonKind` enum + `UnsupportedFlowReason` record |
| `AutomationOptions.cs` | Caller-override options record |
| `AutomationResult.cs` | `AutomationResult`, `TokenSet`, `CapturedCookie`, `Claim`, `AutomationStep` records |
| `AutomationStepLog.cs` | Small mutable accumulator handlers use to build the `Steps` list |
| `LoginPageParser.cs` | AngleSharp-based hidden-field/anti-forgery/username-field extraction + runtime MFA/CAPTCHA/WebAuthn/JS-required detection |
| `RedirectFollower.cs` | Shared redirect-following loop (301/302/303 method-downgrade vs 307/308 preserve; `redirect_uri`-match termination) |
| `AuthorizationCodeFlowHandler.cs` | AuthorizationCode / AuthorizationCodeWithPkce algorithm |
| `RefreshTokenFlowHandler.cs` | RefreshToken algorithm |
| `AuthFlowAutomationEngine.cs` | Public dispatcher/entry point |

Tests (flat, matching existing convention):

| File | Purpose |
|---|---|
| `tests/FakeAuthHttpClient.cs` | Scripted `IAuthHttpClient` — FIFO queue of `(match, responseBuilder)`, builder receives the actual request so it can echo generated values (state) and recompute PKCE `code_challenge` from the posted `code_verifier` |
| `tests/OAuthCryptoHelpers_Tests.cs` | PKCE/state/nonce generator tests |
| `tests/JwtDecoder_Tests.cs` | JWT decode tests (known claims incl. array claim; non-JWT input returns `[]`) |
| `tests/EndpointResolver_Tests.cs` | Discovery / B2C-details / session-fallback / unresolvable cases |
| `tests/LoginPageParser_Tests.cs` | HTML fixtures: hidden-field extraction, relative-action resolution, field-name resolution, MFA/CAPTCHA/WebAuthn/JS-shell detection |
| `tests/AuthFlowAutomationEngine_AuthorizationCodePkce_Tests.cs` | Full scripted B2C AuthCode+PKCE scenarios (see below) |
| `tests/AuthFlowAutomationEngine_RefreshToken_Tests.cs` | Refresh-token scenarios |
| `tests/AuthFlowAutomationEngine_UnsupportedFlows_Tests.cs` | `[Theory]` over out-of-scope `AuthFlowType`s, asserts zero HTTP calls |

`app/sws.csproj` gets `<PackageReference Include="AngleSharp" Version="1.5.1" />` (confirm current stable via `dotnet add package AngleSharp`).

## Core types

```csharp
namespace Automation;

internal interface IAuthHttpClient {
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);
    CookieContainer Cookies { get; }
}
// SystemNetAuthHttpClient : IAuthHttpClient, IDisposable — HttpClientHandler{UseCookies=true, AllowAutoRedirect=false}

internal enum VariableProvenance { Generated, Discovered, Extracted, CallerSupplied }
internal sealed record ResolvedVariable(string Name, string Value, VariableProvenance Provenance, Auth.VariableCategory? OriginalCategory = null, string? DerivedFromVariableName = null, string? Notes = null);

internal enum UnsupportedFlowReasonKind { UnsupportedFlowType, MfaRequired, WebAuthnOrFido2Required, JavaScriptRequired, CaptchaRequired, BrowserAutomationRequired, MissingRequiredEndpoint, MissingCredentials, Other }
internal sealed record UnsupportedFlowReason(UnsupportedFlowReasonKind Kind, string Message, List<string>? Details = null);

internal sealed record AutomationOptions(
    IAuthHttpClient? HttpClient = null, string? UsernameOverride = null, string? PasswordOverride = null,
    string? ClientSecretOverride = null, string? RedirectUriOverride = null, List<string>? ScopesOverride = null,
    string? RefreshTokenOverride = null, int MaxRedirects = 10, int MaxLoginPageHops = 5, TimeSpan? HttpTimeout = null
);

internal sealed record TokenSet(string? AccessToken, string? TokenType, string? IdToken, string? RefreshToken, string? Scope, int? ExpiresInSeconds, DateTimeOffset? ExpiresAtUtc, DateTimeOffset ObtainedAtUtc);
internal sealed record CapturedCookie(string Name, string Value, string? Domain, string? Path, bool Secure, bool HttpOnly, DateTimeOffset? Expires);
internal sealed record Claim(string Name, string Value);
internal sealed record AutomationStep(int Order, string Description, bool Success, DateTimeOffset TimestampUtc, int? HttpStatusCode = null, string? RequestUrl = null);

internal sealed record AutomationResult(
    bool Success, string FlowId, Auth.AuthFlowType FlowType, TokenSet? Tokens, List<CapturedCookie> Cookies,
    List<Claim> Claims, List<AutomationStep> Steps, List<ResolvedVariable> Variables,
    UnsupportedFlowReason? UnsupportedReason, string? ErrorMessage
);
```

Entry point (needs raw `Session`s too, since `DetectedAuthenticationFlow` only carries session IDs and `Discovery` may be null):

```csharp
internal static class AuthFlowAutomationEngine {
    public static Task<AutomationResult> ExecuteAsync(Auth.DetectedAuthenticationFlow flow, IReadOnlyList<Session> sessions, AutomationOptions? options = null, CancellationToken cancellationToken = default);
    public static Task<AutomationResult> RefreshAccessTokenAsync(string tokenEndpoint, string clientId, string? clientSecret, string refreshToken, AutomationOptions? options = null, CancellationToken cancellationToken = default);
}
```

`EndpointResolver.Resolve(flow, sessions)`: `flow.Discovery` endpoints → else `flow.B2cDetails` endpoints → else look up `sessions.FirstOrDefault(s => s.SessionId == flow.AuthorizationRequestSessionId/TokenRequestSessionId)` and strip the query string from `session.Request.Url` → else `null` (handler returns `MissingRequiredEndpoint`, not a throw).

## Algorithm: AuthorizationCode / AuthorizationCodeWithPkce

1. Resolve endpoints; bail with `MissingRequiredEndpoint` if unresolved.
2. Generate fresh `state` (always), `nonce` (if the flow used one), PKCE `code_verifier`/`code_challenge` (via `OAuthCryptoHelpers`, method copied from the flow's captured `code_challenge_method`, default `S256`). Copy through other captured `Configuration` params verbatim (`p`, `response_mode`, `prompt`, `login_hint`, `domain_hint`, `ui_locales`, etc.). Regenerate other `GeneratedPerFlow` params (`correlation_id`, `client-request-id`, `client_info`) as fresh values. Record each as a `ResolvedVariable`.
3. Build + send the authorize `GET` (headers via `ChromeHeadersEngine.BuildHeaders`).
4. **Redirect/response loop** (`RedirectFollower`, bounded by `MaxRedirects`/`MaxLoginPageHops`):
   - 3xx whose `Location` matches `redirect_uri` → parse `code`/`state` from the URL (query or fragment per `response_mode`), validate `state`, done → token exchange.
   - 3xx IdP-internal hop → follow it (301/302/303 downgrade to GET; 307/308 preserve method+body).
   - 200 HTML: if `flow.AuthenticationMethod` is not `UsernamePasswordCredentials` → `MissingCredentials` (SSO was expected but no session cookie worked this run and there's no username/password fallback). Otherwise `LoginPageParser.Parse`: check CAPTCHA/WebAuthn/JS-shell/extra-required-MFA-field markers first (→ matching `UnsupportedFlowReasonKind`); else locate the form (prefers one with a password input), resolve the username field (selector priority from `todo.txt`: id → data-testid → data-* → aria-label → name → role+label → for/label → stable class → hierarchy → nth-child), collect **all** hidden inputs unconditionally, resolve the form action against the page URL. POST hidden fields + username/password (`options.UsernameOverride ?? credentials.Username`, same for password) to the action URL; feed the response back into the same loop. A second password-bearing HTML page after submission → real failure (`Success=false`, not `UnsupportedFlowReason`); a second page with other required fields → `MfaRequired`.
   - Anything else unexpected → `UnsupportedFlowReason.Other`.
5. Token exchange: `POST grant_type=authorization_code, code, redirect_uri (must match step 3 exactly), client_id, code_verifier (if PKCE), client_secret (if RequireClientSecret present, from options or flow's Secret-category variable, else MissingCredentials)`.
6. Parse response → `TokenSet`; non-2xx/`error` present → `Success=false` with `ErrorMessage`, diagnostics still returned (not thrown).
7. `Claims = JwtDecoder.Decode(tokens.IdToken)`; `Cookies` from `httpClient.Cookies`.

## Algorithm: RefreshToken

Single `POST grant_type=refresh_token` (+ scope if requested) to the resolved token endpoint using `options.RefreshTokenOverride ?? flow.Variables["refresh_token"]` (marked `Discovered` + staleness note) and resolved client id/secret. Same response parsing as step 6 above. `RefreshAccessTokenAsync` is the same logic taking endpoint/client info directly (for reuse after an earlier `ExecuteAsync` call, without needing the flow/sessions again).

**Dispatcher**: `switch (flow.FlowType)` → AuthorizationCode/WithPkce → handler; RefreshToken → handler; anything else → immediate `UnsupportedFlowReason.UnsupportedFlowType`, zero HTTP calls.

## Diagnostics rule

`Steps[i].Description` never interpolates a raw secret/token *value* — only names/roles/counts (e.g. `"Received HTML login page; parsed 3 hidden fields including anti-forgery token 'csrf'"`, `"Token response included access_token, id_token, refresh_token"`). `RequestUrl` on a step is always query-stripped. `AutomationResult.Tokens`/`.Cookies`/`.Variables` remain fully populated per the redaction decision above. Add a test assertion that no step description contains a known test password/token value.

## Test strategy highlights

Build the *original* capture via `TestSessionBuilder` (same shape as `AuthFlowDetector_Tests.Detect_IncludesUsernamePasswordCredentials_WhenPresentInAuthCallbackRequest`), run it through the **real** `Auth.AuthFlowDetector.Detect(sessions)` to get a genuine flow — don't hand-construct `DetectedAuthenticationFlow`. Key scenarios: happy-path login-form flow with PKCE cross-check (token-endpoint responder recomputes SHA-256+Base64Url from the posted `code_verifier` and asserts it equals the `code_challenge` captured on the first request), SSO-cookie-sufficient path (no login form), SSO-expected-but-missing-this-run (`MissingCredentials`), rejected credentials (real failure), runtime MFA/CAPTCHA detection, refresh-token flow, and unsupported-flow-type theory test asserting zero HTTP calls. A review note (not an automated test): confirm `SystemNetAuthHttpClient` is never referenced anywhere under `tests/` — that absence is the safety mechanism.

## Implementation order

1. `AngleSharp` package reference.
2. `IAuthHttpClient` + `SystemNetAuthHttpClient`.
3. `OAuthCryptoHelpers` + tests.
4. `JwtDecoder` + tests.
5. `EndpointResolver` + tests.
6. Result/option types (`VariableProvenance`, `UnsupportedFlowReason`, `AutomationOptions`, `AutomationResult` + nested records, `AutomationStepLog`).
7. `tests/FakeAuthHttpClient.cs`.
8. `LoginPageParser` + tests (verify HTML handling in isolation first).
9. `RedirectFollower`.
10. `RefreshTokenFlowHandler` (simplest — smoke-tests the plumbing) then `AuthorizationCodeFlowHandler`.
11. `AuthFlowAutomationEngine` dispatcher.
12. Full scripted integration tests.
13. `dotnet build` + `dotnet test` clean; confirm no test references `SystemNetAuthHttpClient`; manual sanity note that this is library-only (no CLI wiring).

## Verification

- `dotnet build` after steps 6 and 11 to catch drift early.
- `dotnet test` after every phase — existing 54 tests plus all new ones must stay green.
- Grep `tests/` for `SystemNetAuthHttpClient` — must find zero matches (confirms tests never hit real network).
- Manual check: run the full scripted AuthCode+PKCE happy-path test and step through `result.Steps` to confirm no raw secret/token value appears in any description string.
