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
dotnet run -- <path-to-capture.saz> --out plan.json --pretty
```

Options:

- `--out <file>`: write JSON to file (otherwise prints to stdout)
- `--pretty`: indented JSON output (default)
- `--compact`: compact JSON output

## Example

```bash
dotnet run -- ./capture.saz --out ./capture.plan.json --pretty
```

The output is a machine-readable plan you can feed into a later code generator or replay harness.
