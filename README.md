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

Options:

- `--out <file>`: write JSON to file (otherwise prints to stdout)
- default output is indented JSON
- `--compact`: compact JSON output
- `--sources-all`: write-out a plan for obtaining the substrings used in the requests

## Example

```bash
dotnet run --project app/sws.csproj  -- C:/Users/myusername/Desktop/capture.saz --sources-all
dotnet run -- ./capture.saz --out ./capture.plan.json
```

The output is a machine-readable plan you can feed into a later code generator or replay harness.
