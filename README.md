# SAZ Request/Response Planner

This utility scans a Fiddler `.saz` archive and produces a JSON plan describing how to regenerate similar requests and verify responses.

Note: this project uses SAZ/Fiddler terminology, where a "session" means one HTTP request/response exchange.

## Features

### What it extracts per session

- Request line parts (method, target, HTTP version)
- URL reconstruction hints (host + path + query)
- Static vs dynamic headers
- Cookies
- Request body details (format, content type, schema hints)
- Response status, headers, and body details
- Metadata flags/timers from `raw/*_m.xml`
- Suggested regeneration and verification steps

### Automatic B2C/Open-ID Connect/OAuth Detection
- Detects if a session is part of a well-known Open-ID Connect, OAuth, or B2C authentication flow.
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

`--out` is optional; if omitted, output defaults to `<path-to-capture>.sessions.json` next to the input file.

```bash
dotnet run -- <path-to-capture.saz> --out plan.json
```

This produces:

- `<name>.sessions.json` request/response plan
- `<name>.auth.json` detected OIDC/OAuth2/Azure B2C authentication flows: correlated flow groups (type, confidence, related sessions), classified variables (configuration/secret/generated/derived/etc.), replay requirements, and warnings
- `<name>.sources.json` source mapping report

Options:

- `--out <file>`: write JSON to this file instead of the default `<input>.sessions.json`
- default output is indented JSON
- `--compact`: compact JSON output

Note: the sources report is always generated.

## Example

```bash
dotnet run --project app/sws.csproj  -- C:/Users/myusername/Desktop/capture.saz
dotnet run -- ./capture.saz --out ./capture.sessions.json
```

The output is a machine-readable plan you can feed into a later code generator or replay harness.
