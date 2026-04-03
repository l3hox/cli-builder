# cli-builder

Generate agent-ready CLIs directly from SDK type information.

## Problem

AI agents work best with CLI tools — structured output, discoverable commands, composable via pipes. But most SDKs ship without CLIs. Building a CLI by hand for each SDK is tedious, repetitive, and falls out of sync as SDKs evolve.

cli-builder eliminates the manual step: point it at an SDK assembly, get a fully functional CLI back.

## How it works

```
SDK Assembly (.dll)  ──>  cli-builder  ──>  Standalone CLI Project
```

1. **Extract** — The .NET reflection adapter reads the SDK assembly via `MetadataLoadContext` (no code execution) and produces structured `SdkMetadata`: resources, operations, parameters, auth patterns.

2. **Generate** — The C# generator takes `SdkMetadata` and emits a complete CLI project using Scriban templates and System.CommandLine. The output is a standalone project with no cli-builder dependency.

3. **Run** — The generated CLI compiles with `dotnet build` and runs immediately.

## Demo

Generated from the **TestSdk** — real SDK method calls, not stubs:

```bash
$ testsdk-cli --help
Description:
  testsdk-cli — CLI for CliBuilder.TestSdk

Commands:
  customer        order           product

$ testsdk-cli customer get --id cust_42 --json --api-key demo
{
  "id": "cust_42",
  "email": "test@example.com",
  "name": null,
  "status": 0,
  "address": null
}

$ testsdk-cli customer list --json --api-key demo
[
  { "id": "cust_001", "email": "alice@test.com", "status": 0 },
  { "id": "cust_002", "email": "bob@test.com", "status": 1 }
]

$ testsdk-cli product list --json --api-key demo
{ "id": "prod_001", "name": "Widget" }
```

Also validated against the **OpenAI .NET SDK 2.9.1** — 20 resources, 169 operations, zero compile errors. Run `./scripts/demo.sh` to try the TestSdk CLI locally.

## Agent-readiness

Every generated CLI satisfies:

| Requirement | Implementation |
|-------------|---------------|
| Structured output | `--json` flag on every command |
| Human-readable default | Table format when `--json` absent |
| Discoverable commands | `--help` at root, noun, and verb levels |
| Noun-verb structure | `<tool> <resource> <action> [--params]` |
| Semantic exit codes | 0=success, 1=user error, 2=auth error, 3+=SDK error |
| Structured errors | JSON error object to stderr |
| Non-interactive auth | Env var > config file > `--api-key` flag |
| Pipe-friendly | No color when stdout is redirected |

## Generated project structure

```
openai-cli/
+-- openai-cli.csproj          # references OpenAI NuGet + System.CommandLine
+-- Program.cs                 # entry point, --json/--api-key global options
+-- Commands/
|   +-- ChatCommands.cs        # chat complete-chat|get|update|delete|list
|   +-- AssistantCommands.cs   # 26 operations
|   +-- ...                    # one file per resource
+-- Output/
|   +-- JsonFormatter.cs       # --json serialization
|   +-- TableFormatter.cs      # human-readable table
+-- Auth/
    +-- AuthHandler.cs         # env var > config file > flag, credential masking
```

## Test suite

332 tests across 3 projects:

| Project | Tests | Covers |
|---------|-------|--------|
| Generator Tests | 244 | Template rendering, parameter flattening, model mapping, type conversion, sanitization, golden files, compile verification, CanWireSdkCall, multi-arg constructors |
| Core Tests | 52 | Adapter extraction, metadata serialization, type resolution, constructor param detection, nullability |
| Integration Tests | 36 | OpenAI + Stripe SDK extraction and compilation, TestSdk E2E (generate → build → run → assert JSON) |

Run `./scripts/coverage.sh` for a full report.

## Documentation

| Document | Contents |
|----------|----------|
| [docs/cli-builder-spec.md](docs/cli-builder-spec.md) | Full specification -- interfaces, metadata model, config schema, test strategy, scope |
| [docs/ADR.md](docs/ADR.md) | Architecture Decision Records (ADR-001 through ADR-015) |
| [docs/design-notes.md](docs/design-notes.md) | Edge-case policies, behavioral rules, diagnostic codes |
| [docs/FUTURE.md](docs/FUTURE.md) | Out-of-scope ideas and deferred features |
| [AGENTS.md](AGENTS.md) | Quick-start context for AI agents and contributors |
| [docs/process.md](docs/process.md) | Development methodology |
| `docs/internal/` | Agent implementation plans (step-by-step build instructions) |

## Status

Steps 1-8 complete. The generator produces compilable CLIs with real SDK method calls. Multi-arg constructor support enables sub-clients like `ChatClient(string model, ApiKeyCredential cred)` with `--model` as a CLI option.

- **TestSdk:** End-to-end validated — generate, build, run, assert JSON output (12 E2E tests)
- **OpenAI SDK 2.9.1:** 20 resources, 169 operations, 41 wired with real SDK calls, zero compile errors. Live API validated.
- **Stripe.net 51.0.0:** 136 resources, zero compile errors. Second SDK validation proving adapter generality.

**Remaining:** `--json-input` deserialization for complex parameters (unblocks ~78 more OpenAI ops). See [docs/FUTURE.md](docs/FUTURE.md).

**Try it:** `./scripts/demo.sh` (TestSdk) | `OPENAI_APIKEY=sk-... ./scripts/demo-openai.sh` (OpenAI) | `STRIPE_API_KEY=sk_test_... ./scripts/demo-stripe.sh` (Stripe).


## License

Licensed under the [European Union Public Licence v. 1.2](LICENSE) (EUPL-1.2).

SPDX-License-Identifier: `EUPL-1.2`
