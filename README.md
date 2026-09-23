# SAZ Request/Response Planner

This utility scans a Fiddler `.saz` archive and produces a JSON plan describing how to regenerate similar requests and verify responses.

Note: this project uses SAZ/Fiddler terminology, where a "session" means one HTTP request/response pair. This project calls that pair an "exchange" and uses "session" for a series of exchanges.

## Structure

The app is split into sections that stay independent of each other (`tests/SectionDependencies_Tests.cs` enforces this):

| Section | Folder / namespace | Job | May depend on |
| --- | --- | --- | --- |
| Read | `app/Saz` | Reads a `.saz` into memory as a `Session` (a series of `Exchange`s). Faithful to the file: nothing is filtered or classified. | nothing |
| Write JSON | `app/SazJson` | Serializes a `Session` to JSON so humans can read it. | Saz |
| Analyze | `app/Analysis`, `Auth`, `AzureB2c`, `Automation`, `Minimal`, `Qudini` | Everything that interprets exchanges: filtering, flow detection, source reports, replay. | Saz |

`Program.cs` wires the sections together: read, filter, write JSON, then analyze.

## Features

### What it extracts per exchange

- Request line parts (method, target, HTTP version)
- URL reconstruction hints (host + path + query)
- Headers (all of them, as captured)
- Cookies
- Request body details (format, content type, schema hints)
- Response status, headers, and body details
- Metadata flags/timers from `raw/*_m.xml`

### Automatic B2C/Open-ID Connect/OAuth Detection
- Detects if an exchange is part of a well-known Open-ID Connect, OAuth, or B2C authentication flow.
- Identifies which Flow is being used.
- Identifies all variables (cookies, parameters, etc) participating in the flow.
- Identifies any extra variables that do not participate in the flow.
- Identifies if endpoints are available via the "well-known" endpoint.
- Idientifes client id, client secret, and any other required parameter used by the flow.
- Understands which parameters are generated on-the-fly and which ones must be pre-known.
- Generates a results object/class that provides enough details for an authentication engine to replicate the flow.

## Build

```bash
dotnet build
```

## Run

```bash
dotnet run -- <path-to-capture.saz>
```

`--out` is optional; if omitted, output defaults to `<path-to-capture>.exchanges.json` next to the input file.

```bash
dotnet run -- <path-to-capture.saz> --out plan.json
```

This produces:

- `<name>.exchanges.json` request/response plan
- `<name>.auth.json` detected OIDC/OAuth2/Azure B2C authentication flows: correlated flow groups (type, confidence, related exchanges), classified variables (configuration/secret/generated/derived/etc.), replay requirements, and warnings
- `<name>.sources.json` source mapping report

Options:

- `--out <file>`: write JSON to this file instead of the default `<input>.exchanges.json`
- default output is indented JSON
- `--compact`: compact JSON output
- `--trace`: also write `<name>.trace.json` and `<name>.trace.md`. For every form submission found (a POST with name/email/phone fields), lists each value the request needed and which earlier response supplied it (an exchange, a pre-known constant, a browser default, or unknown), following those sources back recursively. See `docs/SNL_TICKET_FLOW_SPEC.md` for a worked example.

Note: the sources report is always generated.

## Example

```bash
dotnet run --project app/sws.csproj  -- C:/Users/myusername/Desktop/capture.saz
dotnet run -- ./capture.saz --out ./capture.exchanges.json
```

The output is a machine-readable plan you can feed into a later code generator or replay harness.
