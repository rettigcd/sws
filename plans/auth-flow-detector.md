# Generic OIDC/OAuth2/Azure B2C Authentication Flow Detector

## Context

`docs/AUTH_FLOW_DETECTION_SPEC.md` describes a feature to replace the current, Azure-specific `AzureB2cAuthenticationScanner` with a generic OIDC/OAuth2 authentication-flow detector that also enriches results with Azure B2C-specific detail. The user flagged that the existing scanner (recently split into `app/AzureB2c/`) "might not be architecturally correct" and authorized reusing, modifying, or replacing it as needed.

Scope decisions already confirmed with the user:
- Build **only the detector/analyzer** (component 1 of the spec's architectural note). The replay engine (component 2) is a distinct follow-up — not built here, but the analyzer's output must carry everything a future replay engine would need.
- The current `<name>.b2c.json` CLI output is **replaced** with the new, richer schema (breaking change to that file's shape, accepted).
- Detection is **generic OIDC/OAuth2 first** (any `/authorize`, `/token`, `/.well-known/openid-configuration`-style endpoint, discovery-doc aware), with Azure B2C as an **enrichment layer** on top (tenant/policy extraction, B2C cookies), not the primary detection path.
- Per spec: do **not** redact anything — display all values including secrets. This means no redaction code is written at all.

## Namespace / Folder Restructure

Split into a generic `Auth` module (primary detection) and a slimmed-down `AzureB2c` module (B2C enrichment only). This is the natural moment to do it since `.b2c.json`'s schema is already breaking.

```
app/Auth/
  RequestType.cs                 (moved from AzureB2c, extended with new grant types)
  ResponseType.cs                (moved from AzureB2c)
  OAuthParameterHelpers.cs       (moved near-verbatim from AzureB2cAuthenticationScanner.cs)
  EndpointClassifier.cs          (generic /authorize, /token, /.well-known/* path matching; discovery-doc aware)
  OidcDiscoveryDocument.cs + DiscoveryDocumentParser.cs
  SessionClassifier.cs           (rewrite of ClassifyRequest/ClassifyResponse/ClassifyUnknownSessions/ClassifySession)
  AuthFlowType.cs
  VariableCategory.cs / VariableSource.cs / Variable.cs
  DetectedAuthenticationFlow.cs
  FlowCorrelator.cs
  VariableExtractor.cs
  ReplayRequirement.cs / ReplayRequirementKind.cs / ReplayRequirementBuilder.cs
  FlowWarning.cs / FlowWarningKind.cs / FlowWarningBuilder.cs
  AuthenticationCredentials.cs   (moved: base + UsernamePasswordCredentials + SessionCookieCredentials)
  AuthFlowDetectionResult.cs     (top-level result — replaces AuthenticationReport)
  AuthFlowDetector.cs            (public entry point: Detect(sessions))

app/AzureB2c/                    (B2C enrichment only, from here on)
  B2cDetector.cs                 (IsB2cHost, policy/tenant/p-param extraction)
  B2cCookieNames.cs              (x-ms-cpim-* recognition, consolidated from CookieSourceService/ReplacementSourceResolver)
  B2cFlowDetails.cs
  B2cEnricher.cs                 (Enrich(DetectedAuthenticationFlow, sessions) -> B2cFlowDetails?)
  # DELETE: AzureB2cAuthenticationScanner.cs, AuthenticationReport.cs, RequestType.cs, ResponseType.cs
```

`app/models/log/Request.cs` / `Response.cs` retype their classification fields from `AzureB2c.RequestType`/`AzureB2c.ResponseType` to `Auth.RequestType`/`Auth.ResponseType` (same enum member names, same `JsonIgnore(WhenWritingDefault)` behavior — `.plan.json` shape unaffected). `app/models/RequestSources.cs` same retype.

## Key Type Designs

```csharp
namespace Auth;

enum RequestType {
    Unknown, Configuration,
    AuthorizationRequest_Unknown, AuthorizationRequest_AuthCodeWithPKCE,
    AuthorizationRequest_AuthCode, AuthorizationRequest_Implicit, AuthorizationRequest_Hybrid,
    AuthorizationRequest_DeviceAuthorization,
    AuthorizationCallbackRequest,
    AuthorizationCodeTokenRequest, RefreshTokenRequest,
    ClientCredentialsTokenRequest, PasswordTokenRequest, DeviceCodeTokenRequest,  // NEW
    EndSessionRequest  // NEW, low priority
}

enum ResponseType {
    Unknown, TokenResponse, AuthorizationRedirect, ErrorResponse,
    DeviceCodeResponse, ConfigurationResponse, SuccessResponse
}

record OidcDiscoveryDocument(
    int SourceSessionId, string? Issuer, string? AuthorizationEndpoint, string? TokenEndpoint,
    string? JwksUri, string? UserInfoEndpoint, string? EndSessionEndpoint, string? DeviceAuthorizationEndpoint,
    List<string> ScopesSupported, List<string> ResponseTypesSupported,
    List<string> GrantTypesSupported, List<string> CodeChallengeMethodsSupported, List<string> ClaimsSupported
);

enum VariableCategory { Configuration, Secret, GeneratedPerFlow, ServerGenerated, Derived, NonParticipating }
enum VariableSource { QueryParameter, FormField, JsonBodyField, RequestHeader, ResponseHeader, Cookie, SetCookie, FragmentParameter, RedirectUrlParameter, Token }

record Variable(
    string Name, string Value, VariableCategory Category, VariableSource Source,
    int SessionId, string? JsonPath, string? DerivedFromVariableName, string? Notes
);

enum AuthFlowType { Unknown, AuthorizationCode, AuthorizationCodeWithPkce, Implicit, Hybrid, ClientCredentials, RefreshToken, DeviceCode, ResourceOwnerPasswordCredentials }

record DetectedAuthenticationFlow(
    string FlowId, AuthFlowType FlowType, double Confidence, List<string> ConfidenceReasons,
    bool IsAzureB2c, B2cFlowDetails? B2cDetails,
    OidcDiscoveryDocument? Discovery,
    int? DiscoveryRequestSessionId, int? AuthorizationRequestSessionId,
    int? AuthorizationCallbackSessionId, int? TokenRequestSessionId,
    List<int> RelatedSessionIds,
    string? Issuer, string? ClientId, string? RedirectUri, List<string> Scopes,
    AuthenticationCredentials? AuthenticationMethod,
    List<Variable> Variables,
    List<ReplayRequirement> ReplayRequirements,
    List<FlowWarning> Warnings
);

enum ReplayRequirementKind { UseDiscoveredEndpoint, GenerateState, GenerateNonce, GenerateCodeVerifier, DeriveCodeChallenge, PreserveClientId, PreserveRedirectUri, PreserveScopes, RequireClientSecret, RequireCodeVerifier, DoNotReuseAuthorizationCode, DoNotReuseTransientCookies, RequireInteractiveLogin, PossibleMfaOrCaptcha, Other }
record ReplayRequirement(ReplayRequirementKind Kind, string Description, string? RelatedVariableName);

enum FlowWarningKind { MissingDiscoveryDocument, IncompleteFlow, MissingCallback, MissingTokenExchange, PkceMismatch, MissingClientSecret, MfaOrCaptchaSuspected, UnsafeImplicitFlow, SensitiveTokenExposure, Other }
record FlowWarning(FlowWarningKind Kind, string Message, IReadOnlyList<int> RelatedSessionIds);

record AuthFlowDetectionResult(
    DateTimeOffset GeneratedUtc, List<DetectedAuthenticationFlow> Flows,
    List<OidcDiscoveryDocument> DiscoveryDocuments,
    List<RequestClassification> SessionClassifications,  // flat per-session list, kept for debugging
    List<FlowWarning> GlobalWarnings
);
```

`AzureB2c.B2cFlowDetails`: `Tenant, Policy, AuthorityBaseUrl, AuthorizationEndpoint, TokenEndpoint, RedirectUri, ClientId, ResponseMode, ResponseType, List<string> Scopes, string? CodeChallenge, string? CodeChallengeMethod, Dictionary<string,string> B2cCookies`.

**Future replay-engine hook**: no code needed now, but `DetectedAuthenticationFlow` already exposes endpoints (discovered or classified), `ClientId`/`RedirectUri`/`Scopes`, PKCE derivation info (`Variables` with `Category=Derived`), and `ReplayRequirements` — the intended consumption contract for component 2. Note this in a doc-comment on `AuthFlowDetectionResult`.

## Flow Correlation Algorithm

Sessions arrive already ordered by `SessionId` ascending (`SazPlanBuilder.Build`'s `OrderBy(kvp => kvp.Key)`) and already classified via `SessionClassifier.ClassifyUnknownSessions`. Matching only looks backward in session order (mirrors the existing `priorSessions` pattern).

1. Bucket classified sessions by `RequestType`: `discovery[]`, `authRequests[]`, `callbacks[]`, `tokenRequests[]` (split by grant type), `deviceAuthRequests[]`.
2. Parse `OidcDiscoveryDocument` from every `discovery` session's response JSON.
3. Seed one flow-in-progress per `authRequest` (capturing state/nonce/client_id/redirect_uri/scope/response_type/code_challenge/code_challenge_method), and one per "headless" `tokenRequest` whose grant type doesn't need a prior authorize step (`client_credentials`, `password`, unlinked `device_code` polls).
4. Match `callbacks -> authRequests`, first match wins, highest confidence first:
   - exact `state` match (query, fragment, or Location-redirect-fragment) → confidence 1.0
   - else callback URL (no query/fragment) equals a candidate's `redirect_uri`, nearest prior unmatched candidate → 0.7
   - else nearest prior unmatched `authRequest` on same host → 0.4 ("sequence-only fallback")
   - attach callback's `code` to the flow
5. Match `tokenRequests -> flows`:
   - `authorization_code`: match by `code` equality (1.0), else nearest prior flow missing a token exchange on same host (0.5)
   - `refresh_token`: match by refresh_token value against a flow's captured token response, else standalone flow + `MissingCallback`-style orphan warning
   - `client_credentials` / `password`: always standalone, 1.0
   - `device_code`: group all polling attempts sharing the same `device_code` into one flow; only the final success/failure response is the flow's result
   - capture token response body fields as `ServerGenerated` variables
6. Match `discovery -> flows`: issuer host equals flow's authorize/token endpoint host, else nearest preceding discovery session on same host; missing entirely → queue `MissingDiscoveryDocument` warning.
7. B2C enrichment pass (additive, doesn't affect `FlowType`/correlation).
8. Confidence = `1.0 - 0.2` per fallback-tier match used, floor `0.1`; `ConfidenceReasons` records every match type used.
9. Any OAuth-shaped session never absorbed into a flow becomes its own partial flow with an appropriate warning (`IncompleteFlow`/`MissingCallback`/`MissingTokenExchange`) — every classified session ends up in exactly one flow. Sessions that never matched any endpoint heuristic never enter flow-building (matches today's `Scan_ExcludesNonOauthB2cSessions` behavior).

## Reuse vs. Rewrite of `AzureB2cAuthenticationScanner.cs`

**Reused near-verbatim** → `Auth/OAuthParameterHelpers.cs`: `TryGetRequestParameter`, `HasResponseType`, `TryParseFragmentParameters`, `HasCodeAndState`, `UrlsMatchIgnoringFragment`, `TryParseFragmentFromLocation`, `IsOauth2_AuthorizationRequest` (already generic — only checks `response_type`+`client_id` presence). The callback-detection-via-prior-authorize-session loop is already generic (calls `IsOauth2_AuthorizationRequest`, not a path check) — ports unchanged into `FlowCorrelator`/`SessionClassifier`.

**Narrow generalization** (only 4 Azure-hardcoded call sites):
- `IsAuthorizeRequest`: `"/oauth2/v2.0/authorize"` → `"/authorize"` (+ exact match against a discovered `authorization_endpoint` for higher confidence)
- `IsTokenRequest`: → `"/token"` (+ discovery-doc match)
- `IsOpenIdConfiguration`: → also accept `/.well-known/oauth-authorization-server`
- `IsDeviceAuthorizationRequest`: → `/devicecode` OR `/device_authorization` OR `/device/code` (+ discovery-doc `device_authorization_endpoint` match)

**Rewritten**: `ClassifyRequest`'s token-endpoint branch gains `client_credentials`, `password`, `device_code` grant-type branches (currently silently fall through to `Unknown` — a real gap vs. spec). `ClassifyResponse` gets the same path generalization. `DetermineOverallAuthenticationMethod` becomes per-flow (parameterized by `flow.RelatedSessionIds`) instead of global-report; B2C-cookie-prefix checks delegate to `AzureB2c.B2cCookieNames` only when `flow.IsAzureB2c`, otherwise infer from `Variable`s already tagged `ServerGenerated`/`Cookie`.

**Entirely new**: discovery doc model/parser, `FlowCorrelator`, `Variable`/`VariableExtractor`, `ReplayRequirementBuilder`, `FlowWarningBuilder`, `AuthFlowDetectionResult` assembly, `AzureB2c.B2cEnricher`/`B2cFlowDetails`.

## Model & Call-Site Changes

- `Request.RequestType` / `Response.ResponseClassification` **stay** as before-the-fact per-session fields (just retyped to `Auth.*`) — they're persisted directly to `.plan.json` and consumed independently by `.sources.json`, which doesn't need cross-session flow grouping. The new `DetectedAuthenticationFlow` grouping is purely additive and only feeds `.b2c.json`.
- `app/Program.cs`: `AzureB2c.AzureB2cAuthenticationScanner.ClassifyUnknownSessions` → `Auth.SessionClassifier.ClassifyUnknownSessions` (same shape). `WriteAzureB2cReportAsJson` body now serializes `Auth.AuthFlowDetectionResult` from `Auth.AuthFlowDetector.Detect(classifiedSessions)` instead of the old `AzureB2c.AuthenticationReport`; file path/extension (`.b2c.json`) unchanged.
- `app/SazPlanBuilder.cs` (`WriteAllSessionSourcesReport`): swap to `Auth.SessionClassifier.ClassifyUnknownSessions`. No shape change to `.sources.json`.
- `app/SourceReportBuilder.cs` (`BuildAzureB2cSourceContext`): swap `AzureB2cAuthenticationScanner.Scan(sessions).Requests.Select(...)` for `Auth.AuthFlowDetector.Detect(sessions).Flows.Where(f => f.IsAzureB2c).SelectMany(f => f.RelatedSessionIds).ToHashSet()`, still computed once outside the per-session loop. `ClassifySession(targetSession, previousSessions)` → `Auth.SessionClassifier.ClassifySession(...)`.
- Method names `ClassifyUnknownSessions`/`ClassifySession` are preserved (moved to `Auth.SessionClassifier`); `Scan` is effectively replaced by `Auth.AuthFlowDetector.Detect` since the return shape fundamentally changes.

## Test Strategy

- **`tests/AzureB2cAuthenticationScanner_Tests.cs`** → split:
  - Port the 10 pure classification tests into `tests/SessionClassifier_Tests.cs` (target `Auth.SessionClassifier`/`Auth.RequestType`/`Auth.ResponseType`). Add: a non-Azure provider case (`login.example.com/connect/authorize` + `/connect/token`, no `oauth2/v2.0` anywhere), and new grant-type cases for `client_credentials`, `password`, `device_code` token requests (currently untested/unclassified).
  - Move the 2 credential-extraction tests into `tests/AuthFlowDetector_Tests.cs` (credentials are now per-flow, need a correlated authorize+callback pair).
- **`tests/SourceReportBuilder_Tests.cs`**: mechanical `AzureB2c` → `Auth` namespace/type swaps; JSON string assertions (enum member names) keep passing unchanged since names are preserved. Re-verify the `{AzureB2C:code_challenge}` source-reference tests after rewiring `BuildAzureB2cSourceContext`.
- **New test files**:
  - `tests/AuthFlowDetector_Tests.cs` — multi-session correlation (state-matched authcode+PKCE happy path, implicit/fragment-mode, generic non-Azure OIDC provider driven by a discovery document, standalone `client_credentials`, standalone/linked `refresh_token`, `device_code` with multiple polls, sequence-fallback low-confidence matching, confidence-score assertions), warning assertions (`PkceMismatch`, `MissingDiscoveryDocument`, `MissingCallback`, `MissingTokenExchange`, `UnsafeImplicitFlow`), replay-requirement assertions per flow type.
  - `tests/VariableClassification_Tests.cs` — one case per category (`client_id`→Configuration, `client_secret`/Basic-auth→Secret, `state`/`nonce`/`code_verifier`→GeneratedPerFlow, `code`/`access_token`/`refresh_token`→ServerGenerated, `code_challenge`→Derived, analytics cookie→NonParticipating).
  - `tests/B2cEnricher_Tests.cs` — tenant/policy extraction (path form and `p=` query form), B2C cookie recognition → `B2cFlowDetails`/`IsAzureB2c=true`, plus a non-B2C-host flow proving zero false positives.
- **Shared `BuildSession` helper**: currently duplicated across test files, missing request-header, fragment, and multi-Set-Cookie support. Extract to `tests/TestSessionBuilder.cs`, add `requestHeaders` and `fragment` parameters (needed for Secret-header and implicit/fragment-mode tests); keep existing params. Note multi-`Set-Cookie` collapsing into one dict key as a pre-existing `Response.Headers` model limitation — not fixed in this pass.
- `Request.Fragment` / `SazPlanBuilder.ExtractFragment` / `AttachRedirectFragments` already exist and work (verified directly) — the `todo.txt` "fragments not recorded" note is likely stale; add a regression test rather than assuming new work is needed.

## Implementation Order

0. **Mechanical relocation**: move `RequestType`/`ResponseType` to `Auth`, retype `Request.cs`/`Response.cs`/`RequestSources.cs`, delete old `AzureB2c/RequestType.cs`/`ResponseType.cs`. Fix call sites to compile.
1. **Endpoint classification**: `OAuthParameterHelpers.cs`, `EndpointClassifier.cs`, `OidcDiscoveryDocument.cs` + parser, `SessionClassifier.cs` (new grant-type branches). Port `SessionClassifier_Tests.cs`. Wire `Program.cs`/`SazPlanBuilder.cs`/`SourceReportBuilder.cs`. Checkpoint: solution compiles, `.plan.json`/`.sources.json` unchanged.
2. **Flow correlation**: `DetectedAuthenticationFlow.cs`, `AuthFlowType.cs`, `FlowCorrelator.cs`, skeleton `AuthFlowDetector.cs`. Correlation tests.
3. **Variable extraction**: `VariableCategory.cs`, `VariableSource.cs`, `Variable.cs`, `VariableExtractor.cs`. `VariableClassification_Tests.cs`.
4. **B2C enrichment**: repurpose `app/AzureB2c/` (`B2cDetector.cs`, `B2cFlowDetails.cs`, `B2cCookieNames.cs`, `B2cEnricher.cs`), wire into `AuthFlowDetector` and `SourceReportBuilder.BuildAzureB2cSourceContext`. `B2cEnricher_Tests.cs`; re-verify `SourceReportBuilder_Tests.cs`.
5. **Replay requirements + warnings + result assembly**: finish `AuthFlowDetector.Detect` orchestration. Extend `AuthFlowDetector_Tests.cs`.
6. **Output wiring + cleanup**: point `.b2c.json` writer at `AuthFlowDetectionResult`; move `AuthenticationCredentials` hierarchy into `Auth/`; delete `AzureB2c/AuthenticationReport.cs` and `AzureB2cAuthenticationScanner.cs`.
7. **Full regression pass**: run whole test suite, fix drift, confirm no redaction code was introduced anywhere.

## Verification

- `dotnet build` after each phase checkpoint (0, 1, 6) to catch compile drift early.
- `dotnet test` after every phase — full suite must stay green.
- End-to-end manual check: run `dotnet run -- <existing-sample.saz> --out plan.json` (a real capture, e.g. from `saz/` if present) and inspect the generated `.b2c.json` for a sane `AuthFlowDetectionResult` shape (flows, variables, replay requirements, warnings) plus confirm `.plan.json`/`.sources.json` are byte-for-byte unchanged in structure (values may still differ if unrelated).
