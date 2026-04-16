# Step 15: Rust Orchestrator — Single `cli-builder` Binary

**Prerequisite:** Steps 13-14 complete (shared Rust core + Python and C# generators). Step 12b complete (Python adapter).
**Output:** A single `cli-builder` Rust binary that orchestrates the full pipeline: adapter subprocess → SdkMetadata JSON → embedded generator → CLI project. Distributed via `cargo install cli-builder`.

---

## Problem

Today the pipeline requires manual plumbing:
```bash
# Python SDK → Python CLI (3 separate commands)
python -m cli_builder_adapter --package stripe --json > /tmp/metadata.json
cargo run -p cli-builder-gen-python -- --input /tmp/metadata.json --output ./output

# .NET SDK → C# CLI (2 separate tools)
dotnet cli-builder inspect --assembly Sdk.dll --json > /tmp/metadata.json
cargo run -p cli-builder-gen-csharp -- --input /tmp/metadata.json --output ./output
```

Step 15 replaces this with:
```bash
cli-builder generate --adapter python --package stripe --generator python --output ./output
cli-builder generate --adapter dotnet --assembly Sdk.dll --generator csharp --output ./output
```

---

## Design

### Architecture

```
cli-builder (Rust binary)
  ├── generate command
  │     ├── invoke adapter subprocess → capture JSON stdout
  │     ├── deserialize AdapterResultEnvelope
  │     ├── call embedded generator (library function, not subprocess)
  │     └── print diagnostics, return exit code
  └── inspect command
        ├── invoke adapter subprocess → capture JSON stdout
        ├── if --json: pass through to stdout
        └── else: human-readable summary
```

**Generators are embedded as library calls** — not subprocesses. Both `cli-builder-gen-python` and `cli-builder-gen-csharp` are Rust library crates already. The orchestrator depends on them directly. This gives single-binary distribution with no IPC overhead.

**Adapters are subprocesses** — per ADR-016. Each adapter is a standalone executable in its native language:
- `.NET adapter`: uses the .NET tool invoked as `cli-builder-dotnet inspect --assembly X --json` (renamed from `cli-builder` to avoid binary name collision — see below)
- `Python adapter`: uses `python -m cli_builder_adapter --package X --json`

### Binary name collision (council fix)

The Rust binary is named `cli-builder`. The .NET adapter was also `cli-builder`. Installing both shadows one on PATH. Resolution:
- **.NET adapter binary renamed** to `cli-builder-dotnet` (via `<ToolCommandName>cli-builder-dotnet</ToolCommandName>` in csproj)
- **Environment variable overrides** available from Phase 1: `CLI_BUILDER_DOTNET_ADAPTER` and `CLI_BUILDER_PYTHON_ADAPTER` let users point to adapter binaries explicitly
- Post-Step 15: the .NET `cli-builder` tool's `generate` command is deprecated (Rust binary handles generation). The .NET tool becomes `cli-builder-dotnet` with `inspect` only.

### CLI interface

```
cli-builder generate [OPTIONS]

Adapter selection (one required):
  --adapter dotnet    Use .NET reflection adapter
  --adapter python    Use Python inspect adapter

Adapter arguments:
  --assembly <PATH>   .NET SDK assembly path (dotnet adapter)
  --package <NAME>    Python package name (python adapter)
  --module <NAME>     Python module within package (optional)

Generator selection:
  --generator csharp  Generate System.CommandLine C# CLI (default for dotnet adapter)
  --generator python  Generate click-based Python CLI (default for python adapter)

Output:
  --output <DIR>      Output directory (required)
  --cli-name <NAME>   CLI name (derived from SDK name if omitted)
  --overwrite         Replace existing output directory

C# generator options:
  --sdk-project-path <PATH>  Local SDK .csproj (ProjectReference instead of PackageReference)
```

```
cli-builder inspect [OPTIONS]

  --adapter dotnet --assembly <PATH>
  --adapter python --package <NAME> [--module <NAME>]
  --json              Output raw JSON envelope (default: human-readable summary)
```

### Adapter subprocess contract

| Adapter | Command | Stdout | Stderr | Exit codes |
|---------|---------|--------|--------|------------|
| dotnet | `cli-builder-dotnet inspect --assembly <path> --json` | AdapterResultEnvelope JSON | Diagnostics | 0/1/2 |
| python | `python -m cli_builder_adapter --package <name> [--module <name>] --json` | AdapterResultEnvelope JSON | Diagnostics | 0/1/2 |

The orchestrator:
1. Spawns the adapter subprocess
2. Captures stdout (JSON) and stderr (diagnostics)
3. Parses stdout as `AdapterResultEnvelope`
4. On exit code 2: report environment failure, abort
5. On exit code 1: report errors, continue with degraded metadata (see invariants below)
6. On exit code 0: proceed to generation

**Edge case handling** (council fix):
- **Truncated/partial JSON on stdout**: if `serde_json::from_str` fails, emit structured error `{"error": {"code": "adapter_output_error", "message": "..."}}` to stderr and exit 1.
- **Empty stdout with exit code 0**: treat as adapter bug — emit error diagnostic and exit 1.
- **Adapter subprocess timeout**: 30s default. Kill subprocess, emit timeout error, exit 1.

### Degraded metadata invariants (council fix)

When an adapter exits with code 1 (partial failure), the `AdapterResultEnvelope` is still parseable JSON but may have incomplete data. The orchestrator guarantees:
- `metadata.name` and `metadata.version` are always present (adapters must emit these even on error)
- `metadata.resources` may be empty or incomplete (fewer resources than expected)
- `metadata.auth_patterns` may be empty
- `diagnostics` contains at least one Error-severity diagnostic explaining what failed
- The generator receives the metadata as-is. If a required field is missing, the generator emits its own diagnostic and produces partial output rather than panicking.

### Adapter discovery

The orchestrator finds adapter binaries via (checked in order):
1. **Environment variables** (highest priority): `CLI_BUILDER_DOTNET_ADAPTER`, `CLI_BUILDER_PYTHON_ADAPTER`
2. **PATH lookup** — `cli-builder-dotnet` (for .NET adapter) and `python` / `python3` (for Python adapter)
3. Future: `cli-builder.toml` config file

### Diagnostics output

Stderr diagnostics formatted with severity, code, and message:
```
[INFO]    CB601: Package 'stripe' imported at runtime — side effects may occur
[WARNING] CB301: Required parameter 'name' only accessible via --json-input
[ERROR]   CB600: Could not import package 'nonexistent'
```

Color when stderr is a TTY (red=error, yellow=warning, dim=info). No color when redirected.

---

## Implementation Order

### Phase 1: Orchestrator crate + generate command + adapter tests

1. Create `crates/cli/` crate (the main binary)
2. Add to workspace `Cargo.toml`
3. Depends on: `cli-builder-core`, `cli-builder-gen-python`, `cli-builder-gen-csharp`, `clap`, `serde_json`
4. `src/main.rs` — clap CLI with `generate` and `inspect` subcommands
5. `src/adapter.rs` — subprocess invocation: spawn adapter, capture stdout/stderr, parse JSON, check exit code. **Env-var override from day one** (`CLI_BUILDER_DOTNET_ADAPTER`, `CLI_BUILDER_PYTHON_ADAPTER`).
6. `src/generate.rs` — generate command: invoke adapter → call embedded generator → print diagnostics
7. `src/diagnostics.rs` — DiagnosticsFormatter (colored stderr output)
8. **Mock/fixture adapter scripts** — minimal scripts that emit canned JSON + exit codes. Used for all adapter tests:
   - `test_fixtures/adapter_ok.sh` — emit valid AdapterResultEnvelope JSON, exit 0
   - `test_fixtures/adapter_degraded.sh` — emit partial JSON with Error diagnostic, exit 1
   - `test_fixtures/adapter_fail.sh` — emit error JSON, exit 2
   - `test_fixtures/adapter_bad_json.sh` — emit truncated JSON, exit 0
   - `test_fixtures/adapter_empty.sh` — emit nothing, exit 0
   - `test_fixtures/adapter_timeout.sh` — sleep forever (for timeout test)
9. Tests (all in Phase 1, not deferred):
   - Adapter exit code 0 → generation proceeds
   - Adapter exit code 1 → degraded metadata, diagnostics printed, generation proceeds
   - Adapter exit code 2 → abort, structured error
   - Adapter bad JSON → structured error, exit 1
   - Adapter empty stdout → error diagnostic, exit 1
   - E2E: `generate` with fixture adapter → output files exist
   - DiagnosticsFormatter: color/no-color, severity grouping

### Phase 2: inspect command + human-readable output

1. `src/inspect.rs` — inspect command: invoke adapter → print summary or pass-through JSON
2. Human-readable summary: SDK name, version, resource count, auth detection, resource list with operation counts
3. `--json` flag: pass through raw adapter JSON to stdout
4. E2E test: inspect with fixture adapter → summary output correct

### Phase 3: Adapter discovery + timeout

1. PATH lookup for adapter binaries (`cli-builder-dotnet`, `python3`, `python`)
2. Adapter not found → structured error with install instructions
3. Timeout handling: 30s default, kill subprocess on timeout
4. Test: adapter not found error message
5. Test: timeout with stalling fixture adapter

### Phase 4: Distribution + documentation

1. `cargo install cli-builder` — ensure the binary name is `cli-builder`
2. Rename .NET tool to `cli-builder-dotnet` (`<ToolCommandName>` in csproj)
3. Document that .NET `cli-builder` `generate` command is deprecated — use Rust binary
4. Update README with unified CLI usage
5. Update AGENTS.md, FUTURE.md
6. Shell completions via clap (bash, zsh, fish)

---

## Key files

| File | Purpose |
|------|---------|
| `cli-builder/Cargo.toml` | Main binary crate, depends on core + both generators |
| `cli-builder/src/main.rs` | clap CLI with generate/inspect subcommands |
| `cli-builder/src/adapter.rs` | Subprocess adapter invocation + env-var override |
| `cli-builder/src/generate.rs` | Generate command orchestration |
| `cli-builder/src/inspect.rs` | Inspect command |
| `cli-builder/src/diagnostics.rs` | Colored stderr diagnostics formatter |
| `cli-builder/test_fixtures/` | Mock adapter scripts for testing |

---

## Verification

```bash
# Build
cd crates && cargo build

# Generate C# CLI from .NET SDK
cli-builder generate --adapter dotnet --assembly path/to/Sdk.dll --generator csharp --output ./output

# Generate Python CLI from Python SDK
cli-builder generate --adapter python --package stripe --generator python --output ./output

# Inspect SDK metadata
cli-builder inspect --adapter python --package stripe
cli-builder inspect --adapter python --package stripe --json

# Override adapter binary location
CLI_BUILDER_DOTNET_ADAPTER=/path/to/cli-builder-dotnet cli-builder generate --adapter dotnet ...

# Install globally
cargo install --path crates/cli-builder
```

---

## Risk

**Low.** The hard work (adapters, generators, shared core) is done. This step is plumbing:
- Subprocess invocation is well-understood
- Generators are already library calls
- The JSON contract is proven (658 tests validate it)

**Main risks:**
- **.NET adapter rename** — changing `<ToolCommandName>` from `cli-builder` to `cli-builder-dotnet` is a breaking change for existing users. Mitigated by env-var override and clear migration docs.
- **Cross-platform subprocess** — `std::process::Command` works on all platforms, but PATH and shell behavior differs. Test on Windows/macOS/Linux.
- **Binary size** — embedding both generators increases the binary. Tera templates are compiled in via `include_str!`. Expect ~5-10MB binary.

---

## What this does NOT solve

- Standalone .NET adapter binary (currently uses the full .NET tool — could be split to extract-only later)
- New language generators (Kotlin, Go, TypeScript — future)
- Config file (`cli-builder.toml`) for per-SDK customization
- Agent enrichment (`--enrich` flag with LLM provider)
- CI/CD pipeline (GitHub Actions, Docker image)
