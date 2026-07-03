# Minimal namespace — guidelines

Running list of standing instructions for the `Minimal` namespace (a minimal-parameter / minimal-dependency
B2C authentication session, distinct from the fuller `Automation` engine). Update this file as new general
instructions are given; one-off feature requests (e.g. "add a step that does X") don't belong here.

- **Design goal**: minimal parameters, minimal dependencies. Prefer self-contained code in `Minimal` over
  reusing types from `Automation` that carry extra parameters/coupling — e.g. `Automation.OAuthCryptoHelpers`
  was judged too coupled, so `Minimal` has its own `Crypto.cs` instead.
  - Exception: `Automation.IAuthHttpClient` is reused directly as the HTTP abstraction rather than
    redefining an equivalent interface.

- **`Context`**:
  - Holds all variables required to complete B2C authentication. Passed to static `Steps` methods. Also
    holds results produced by steps.
  - All properties start out `null` and are set after instantiation (no `required` members) — population
    happens via `AuthSession` initialization methods and via `Steps` methods as the flow progresses, not at
    construction time.
  - When a new `Steps` method needs additional data, add the corresponding property to `Context` and
    comment its purpose.

- **`Steps`**:
  - Static class. Each method has the signature `Task<StepResult> DoSomethingAsync(Context ctx)`.
  - Each method must have an XML `/// <returns>` doc comment listing every `StepResult` value that method
    can actually return (not the whole enum — just the ones relevant to that method).
  - There is no `StepResult.Failed` (removed). Any failure — a missing precondition (e.g. a prior step's
    output wasn't populated on `Context`), a non-success HTTP response, an unparseable response, an
    unexpected response shape, etc. — throws an exception. `StepResult` values only ever represent
    expected, successful flow branches.

- **`StepResult`**:
  - Prefer specific, descriptive result names over generic ones (e.g. `WellKnownConfigRetrieved` rather
    than `Success`) so each value's meaning is unambiguous without reading the method that produced it.

- **`AuthSession`**:
  - The only public entry point into `Minimal`. `Context` is `internal`, so callers never see or construct
    it directly — `AuthSession` owns a private `Context` and exposes it piecemeal.
  - Configuration is exposed via small public `Init*` methods (e.g. `InitAuthorizationRequest(...)`,
    `InitAuthorization(username, password)`), each setting one related group of `Context` properties, rather
    than exposing `Context` itself or a single do-everything constructor/method.
  - Results are exposed the same piecemeal way, via small public read-only properties that forward to the
    corresponding `Context` property (e.g. `Tokens => _context.Tokens`), rather than returning them from
    `AuthenticateAsync` or exposing `Context` itself. Any type exposed this way (e.g. `TokenResponse`) must
    itself be `public`, not `internal`.
  - `Automation.IAuthHttpClient` is `public` (not `internal`, unlike the rest of `Automation`) specifically
    so `AuthSession`'s public constructor can take it as a parameter.
  - Minimal required inputs to run `AuthenticateAsync()`: the constructor's `IAuthHttpClient`, the four
    `InitAuthorizationRequest` values (`wellKnownEndpoint`, `clientId`, `redirectUri`, `scope`), and either
    `InitAuthorization(username, password)` or a pre-seeded SSO session via `InitAuthServerCookies(cookies)`
    (which lets `SendAuthorizationRequestAsync` redirect straight through without a login page). Everything
    else on `Context` (`State`, `CodeVerifier`, `CodeChallenge`, `Code`, `LoginPageHtml`/`LoginPageUrl`,
    `WellKnown`, `Tokens`) is generated or populated internally by `Steps`, not caller-supplied.
