# SAZ Request/Response Planner

This utility scans a Fiddler `.saz` archive and produces a JSON plan describing how to regenerate similar requests and verify responses.

## What it extracts per session

- Request line parts (method, target, HTTP version)
- URL reconstruction hints (host + path + query)
- Static vs dynamic headers
- Cookies
- Request body details (format, content type, schema hints)
- Response status, headers, and body details
- Metadata flags/timers from `raw/*_m.xml`
- Suggested regeneration and verification steps

## Build

```bash
dotnet build
```

## Run

```bash
dotnet run -- <path-to-capture.saz> --out plan.json
```

This produces:

- `<name>.plan.json` request/response plan
- `<name>.plan.b2c.json` OAuth/B2C request list in session order with `SessionId` and `RequestType`
- `<name>.plan.sources.json` source mapping report

Options:

- `--out <file>`: write JSON to file (otherwise prints to stdout)
- default output is indented JSON
- `--compact`: compact JSON output

Note: the sources report is always generated.

## Example

```bash
dotnet run --project app/sws.csproj  -- C:/Users/myusername/Desktop/capture.saz
dotnet run -- ./capture.saz --out ./capture.plan.json
```

The output is a machine-readable plan you can feed into a later code generator or replay harness.
