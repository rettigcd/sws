# Snl namespace — guidelines

Running list of standing instructions for the `Snl` namespace. Update this file as new general
instructions are given; one-off feature requests (e.g. "add a step that does X") don't belong here.

- **Design goal**: minimal parameters, minimal dependencies. Prefer self-contained code in `Snl` over
  reusing types from `Automation` that carry extra parameters/coupling.
  - Exception: `Automation.IAuthHttpClient` is reused directly as the HTTP abstraction rather than
    redefining an equivalent interface.

- **`Context`**:
  - Holds all variables required to complete the session. Passed to static `Steps` methods. Also
    holds results produced by steps.
  - All properties start out `null` and are set after instantiation (no `required` members) — population
    happens via `TicketSession` initialization methods and via `Steps` methods as the flow progresses, not at
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

- **`TicketSession`**:
  - The only public entry point into `Snl`. `Context` is `internal`, so callers never see or construct
    it directly — `TicketSession` owns a private `Context` and exposes it piecemeal.
  - Configuration is exposed via small public `Init*` methods, each setting one related group of `Context`
    properties, rather than exposing `Context` itself or a single do-everything constructor/method.
  - Results are exposed the same piecemeal way, via small public read-only properties that forward to the
    corresponding `Context` property, rather than returning them from the main flow method or exposing
    `Context` itself. Any type exposed this way must itself be `public`, not `internal`.
  - `Automation.IAuthHttpClient` is `public` (not `internal`, unlike the rest of `Automation`) specifically
    so `TicketSession`'s public constructor can take it as a parameter.
  - The sequence of requests `TicketSession` drives is based on the captured flow in
    `/saz/snl3/snl3.sessions.json` — each `Steps` method should correspond to one (or a related group)
    of the sessions recorded there, in the same order.
