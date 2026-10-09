# Capture-Specific SPA Replay Server --- Requirements Specification

## 1. Objective

Build a small local C# web server that reproduces one specific captured
web application closely enough to test a Tampermonkey script against it
when the real server is unavailable.

A specific Fiddler `.saz` capture will be supplied to Claude Code during
development. The goal is **not** to build a generic SAZ replay server.
Correctly reproducing the supplied captured application is the priority.

Claude Code should inspect the supplied SAZ, understand the
application's request/response behavior, and implement the simplest
reliable local simulation of that specific application.

## 2. Primary Use Case

The developer has:

-   a Fiddler SAZ capture of an existing SPA
-   a Tampermonkey script intended to run against that SPA
-   no reliable access to the original server

The replay server should allow the SPA to load and behave locally using
the captured responses so that the Tampermonkey script can be developed
and tested.

## 3. Implementation

-   Use C# targeting .NET 8.
-   All C# source code must be contained in **one `.cs` file**.
-   The server may use ASP.NET Core/.NET 8 facilities.
-   Supporting captured data does not need to be embedded in the C#
    source.
-   A `replay-data` directory or similar supporting directory is
    expected and acceptable.
-   Favor simple, understandable code over a generalized framework.

Example final layout:

``` text
SnlReplay.cs
replay-data/
    ...
```

Additional non-C# support files may be generated when useful.

## 4. Role of the SAZ File

The SAZ file is **development/build input**, not runtime input.

Claude Code may:

-   inspect the SAZ
-   unzip/extract it
-   parse Fiddler session files
-   convert captured responses into simpler formats
-   rename extracted resources
-   create metadata/index files
-   reorganize captured data
-   modify captured HTML or JavaScript when useful for local replay
-   discard captured information that is unnecessary for reproducing
    this application

The finished replay server must **not require the original `.saz`
file**.

Do not build a general-purpose runtime SAZ parser unless inspection of
this particular capture shows that doing so is genuinely the simplest
solution.

## 5. Capture-Specific Design

Claude Code should analyze the supplied capture before deciding how to
implement replay.

Do not assume that generic handling is required for:

-   arbitrary websites
-   arbitrary SAZ files
-   arbitrary hosts
-   arbitrary authentication systems
-   every possible HTTP header
-   every possible URL format
-   every browser security mechanism

Determine what this specific SPA actually needs and implement that.

Generalize code where doing so is simple and natural, but do not add
complexity merely to support hypothetical future SAZ files.

## 6. Starting Page

Claude Code should determine the correct SPA starting page by inspecting
the supplied capture.

The finished server should use that known starting page.

There is no requirement for the user to select from captured HTML pages
at runtime.

When the server starts, print the local starting URL.

Example:

``` text
Replay server running at http://localhost:8123/
```

The actual local route structure may differ if required by the captured
application.

## 7. Command-Line Options

The server should support:

``` text
--port <port>
--open
```

### Port

If `--port` is supplied, use that localhost port.

If it is omitted, automatically choose an available localhost port.

### Browser Launch

If `--open` is supplied, launch the application's local starting URL in
the system default browser.

Without `--open`, print the URL but do not launch a browser.

### Shutdown

`Ctrl+C` should cleanly terminate the server.

## 8. Replay Data

Claude Code may choose whatever representation makes the captured data
easiest to replay.

Examples include:

``` text
replay-data/
    index.html
    assets/
    api/
    responses/
    metadata.json
```

or any other sensible structure.

Captured responses do not need to remain in Fiddler's original raw
format.

For example, a captured JSON response may simply become:

``` text
replay-data/responses/orders-001.json
```

if that makes the implementation clearer.

The objective is accurate simulation of the captured application, not
preservation of the SAZ's internal representation.

## 9. Request Identity

For captured requests that need replay sequencing, identify requests
using:

-   HTTP method
-   original host
-   path
-   exact query string

Do not use the request body for matching in v1.

Treat HTTP and HTTPS versions of the same host/path/query as the same
replay identity unless analysis of the supplied application demonstrates
that this would be incorrect.

Query matching should be exact.

Do not introduce fuzzy query matching unless required by the supplied
capture and explicitly documented in the implementation.

## 10. Repeated Captured Requests

When the capture contains multiple exchanges with the same request
identity, replay their responses in captured order.

If captured responses are:

``` text
A
B
C
```

successive matching requests should receive:

``` text
Request 1 -> A
Request 2 -> B
Request 3 -> C
Request 4 -> C
Request 5 -> C
...
```

After all captured versions have been served, continue returning the
final captured version.

Replay state exists only in memory and resets when the server restarts.

The implementation must be safe if overlapping browser requests occur.
Two simultaneous requests must not accidentally consume the same next
response.

## 11. URL and Origin Handling

Do **not** implement a complicated generic URL-rewriting system merely
because one might be needed for arbitrary websites.

Instead:

1.  Inspect the supplied SPA and its captured traffic.
2.  Identify the hosts/origins actually used.
3.  Determine how its HTML and JavaScript generate requests.
4.  Implement the simplest URL rewriting, routing, or local mapping
    necessary to make this application work.

Absolute external URLs used by the SPA must ultimately route to the
local replay server rather than the unavailable real servers.

Relative URLs should normally remain relative when that naturally causes
them to reach the replay server.

If the application uses multiple original hosts, implement an
appropriate capture-specific mapping so requests can still be associated
with the correct captured host.

It is acceptable to preprocess or modify extracted HTML/JavaScript in
`replay-data` if doing so produces a simpler and more reliable solution
than dynamically rewriting every response.

## 12. Response Fidelity

Reproduce captured behavior sufficiently accurately for the SPA and
Tampermonkey script to function.

Preserve when relevant:

-   HTTP status codes
-   response bodies
-   content types
-   useful response headers
-   cookies
-   request ordering/state behavior

Do not preserve captured headers blindly.

Remove, rewrite, or recalculate headers when necessary for localhost
replay.

Examples may include:

-   `Content-Length`
-   compression headers
-   `Content-Security-Policy`
-   CORS headers
-   cookie domain restrictions
-   origin/host-specific security headers

Successful local simulation takes precedence over byte-for-byte HTTP
fidelity.

## 13. Compression

If captured responses are compressed, Claude Code may decompress them
during preprocessing and store the decompressed content in
`replay-data`.

There is no requirement to reproduce the original wire compression.

The browser simply needs to receive a valid representation of the
captured content.

## 14. Cookies and Browser Security

Inspect what this particular application actually requires.

If captured cookies are needed, adapt them for localhost as necessary.

If CSP, CORS, cookie-domain rules, or similar browser security policies
prevent local operation, remove or relax the relevant captured
restrictions.

Do not build a generalized cookie or browser-security policy engine.

## 15. Unknown Requests

Requests that cannot be mapped to a known captured/local resource
should:

-   return HTTP 404
-   be clearly logged to the console

Example:

``` text
404 UNMATCHED: GET /api/something
```

Do not silently contact the original server.

The local replay environment should remain self-contained.

## 16. Console Logging

Log incoming requests and their replay decisions clearly enough to
diagnose failures.

For sequential captured responses, useful output would resemble:

``` text
GET api.example.com/orders?id=123 -> response 1 of 3
GET api.example.com/orders?id=123 -> response 2 of 3
GET api.example.com/orders?id=123 -> response 3 of 3
GET api.example.com/orders?id=123 -> response 3 of 3 [reused]
```

For static assets, concise logging is sufficient.

For misses:

``` text
404 UNMATCHED: GET /missing/resource
```

Exact formatting is not prescribed.

## 17. Verification Is Part of the Task

Claude Code should not consider the task complete merely because the C#
source compiles.

After implementation, Claude Code should:

1.  compile the server
2.  run it
3.  load/request the application's starting page
4.  exercise the captured application's resources and endpoints as far
    as practical
5.  inspect server output for unmatched requests
6.  identify missing resources, routing mistakes, URL-rewrite problems,
    and obvious response problems
7.  fix discovered replay issues
8.  repeat until the captured application is being served successfully
    to the extent that can reasonably be verified in the development
    environment

If browser automation or an available browser inspection mechanism can
be used, Claude Code should use it when practical to verify that the SPA
loads without obvious resource failures.

The goal is a **working simulation**, not merely generated source code.

## 18. Capture Analysis

Before or during implementation, Claude Code should inspect the supplied
SAZ sufficiently to understand at least:

-   the SPA's starting document
-   original hosts/origins
-   JavaScript bundles
-   relevant static assets
-   API endpoints
-   repeated API calls
-   redirects, if relevant
-   cookies, if relevant
-   response content types
-   URLs/origins embedded in HTML or JavaScript
-   which captured requests are necessary for normal startup
-   whether any captured requests can safely be ignored

Implementation decisions should be based on this evidence rather than
assumptions about generic SPAs.

## 19. Simplification Is Encouraged

Claude Code is explicitly allowed to simplify the captured application
representation.

For example, if the original capture contains complicated Fiddler
response files but the SPA only needs a JSON body and status code,
extract that information into a straightforward replay file.

Likewise, if replacing an original API origin inside one extracted
JavaScript bundle is much simpler than implementing runtime origin
rewriting, that is acceptable.

Prefer the simplest solution that faithfully supports the supplied
application.

## 20. SPA Routing

Determine whether this specific SPA requires client-side history
fallback.

If it does, implement the minimum fallback needed for this application.

Do not build a generalized SPA-routing subsystem unnecessarily.

This replaces the earlier generic requirement that unknown navigation
routes must always be 404: behavior should now be based on what this
specific captured SPA requires.

## 21. Future Response Modification

Future versions may need developer-controlled behavior such as:

-   changing captured response data
-   intentionally delaying responses
-   returning HTTP 500 errors
-   replacing a response
-   changing response headers
-   selecting a particular captured response

Do **not** implement these features in v1.

Do not build a plugin framework or generalized modification engine now.

However, avoid unnecessarily entangling routing and response data so
badly that later response modification would require rewriting the
entire application.

## 22. Non-Goals

The following are explicitly not goals:

-   replaying arbitrary SAZ files
-   accepting a SAZ filename at runtime
-   implementing a general Fiddler-compatible replay engine
-   supporting arbitrary websites
-   preserving the original SAZ format
-   byte-for-byte HTTP replay
-   proxying unknown requests to the real application
-   production deployment
-   multi-user operation
-   security against hostile clients
-   persistent replay state
-   request-body matching
-   generalized fault injection
-   generalized response modification
-   unnecessary configuration frameworks
-   unnecessary abstraction intended only for hypothetical future
    captures

## 23. Expected Final Deliverable

At minimum:

``` text
SnlReplay.cs
replay-data/
    ...
```

`SnlReplay.cs` contains **all C# source code**.

`replay-data` contains whatever extracted or transformed captured
resources are needed.

If a build command or small project metadata file is necessary to
compile/run the .NET 8 application, that is acceptable, but do not split
the C# implementation across multiple source files.

The original SAZ must not be required after the
development/preprocessing work is complete.

## 24. Expected Usage

Typical final usage:

``` text
SnlReplay --open
```

or:

``` text
SnlReplay --port 8123 --open
```

The server:

1.  loads its prepared replay data
2.  starts on localhost
3.  prints the starting URL
4.  optionally opens the browser
5.  serves the captured SPA
6.  replays captured dynamic responses in the required sequence
7.  logs requests and unmatched resources
8.  runs until `Ctrl+C`

## 25. Acceptance Criteria

The implementation is complete when:

1.  All C# application code exists in one `.cs` file.
2.  The supplied SAZ has been analyzed rather than treated as an unknown
    generic input.
3.  Required captured resources/responses have been extracted or
    transformed into supporting replay data.
4.  The finished application no longer requires the SAZ file.
5.  The server starts successfully on localhost.
6.  An available port is selected automatically when `--port` is
    omitted.
7.  `--port` selects a requested port.
8.  `--open` launches the local SPA in the default browser.
9.  The correct starting page for the supplied application is served.
10. Required HTML, JavaScript, assets, and API responses used by this
    captured SPA are served locally.
11. Requests do not depend on the unavailable original servers.
12. External origins actually used by the captured SPA are
    mapped/reworked appropriately for localhost.
13. Request replay identity uses method + original host + path + exact
    query string where sequential capture matching is required.
14. Request bodies are not required for matching.
15. Repeated captured requests return responses sequentially in capture
    order.
16. Once a repeated sequence is exhausted, its final response is reused.
17. Replay sequencing is safe for overlapping requests.
18. Relevant captured status codes, bodies, content types, cookies, and
    headers are reproduced or adapted sufficiently for the SPA to
    operate.
19. Captured security/domain restrictions that prevent localhost
    operation are adjusted where necessary.
20. Unknown requests return 404 and are clearly logged.
21. The implementation has actually been run and tested rather than only
    compiled.
22. Obvious missing resources, routing failures, and replay problems
    discovered during testing have been corrected.
23. Replay state resets when the server restarts.
24. `Ctrl+C` cleanly terminates the server.
25. No generic SAZ replay framework or other unnecessary v1
    functionality has been built.
26. The resulting local site is sufficiently functional to use as a test
    target for the intended Tampermonkey script.
