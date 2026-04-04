# cli-builder

Generate agent-ready CLIs directly from .NET SDK assemblies.

## Problem

AI agents work best with CLI tools — structured output, discoverable commands, composable via pipes. But most SDKs ship without CLIs. Building a CLI by hand for each SDK is tedious, repetitive, and falls out of sync as SDKs evolve.

cli-builder eliminates the manual step: point it at an SDK assembly, get a fully functional CLI back.

## Try it now

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bash
git clone https://github.com/your-org/cli-builder.git
cd cli-builder
dotnet build

# Generate and run the TestSdk demo CLI:
./scripts/demo.sh

# Generate and run against Stripe (with test key):
STRIPE_API_KEY=sk_test_... ./scripts/demo-stripe.sh

# Generate and run against OpenAI:
OPENAI_APIKEY=sk-... ./scripts/demo-openai.sh
```

> **Note:** cli-builder is currently a library. The `cli-builder generate --assembly X.dll` CLI entry point is in active development ([Step 10](docs/FUTURE.md)).

## How it works

```
SDK Assembly (.dll)  ──>  cli-builder  ──>  Standalone CLI Project
```

1. **Extract** — The .NET reflection adapter reads the SDK assembly via `MetadataLoadContext` (no code execution) and produces structured `SdkMetadata`: resources, operations, parameters, auth patterns.

2. **Generate** — The C# generator takes `SdkMetadata` and emits a complete CLI project using Scriban templates and System.CommandLine. The output is a standalone project with no cli-builder dependency.

3. **Run** — The generated CLI compiles with `dotnet build` and runs immediately. Generated handlers make real SDK method calls.

## Validated SDKs

| SDK | Resources | Operations wired | Live API tested |
|-----|-----------|-----------------|----------------|
| TestSdk | 6 | 100% | Yes (15 E2E tests) |
| OpenAI 2.9.1 | 20 | 41/169 (24%) | Yes (`get-models`, `get-model`) |
| Stripe.net 51.0.0 | 196 | ~93% | Yes (`payment-intent list`, `product create`, `price create`, etc.) |

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

## Test suite

347 tests across 3 projects:

| Project | Tests | Covers |
|---------|-------|--------|
| Generator Tests | 252 | Template rendering, model mapping, type conversion, sanitization, golden files, compile verification, nullable guards |
| Core Tests | 52 | Adapter extraction, type resolution, constructor detection, collision resolution |
| Integration Tests | 43 | OpenAI + Stripe extraction/compilation, TestSdk E2E, --json-input, namespace disambiguation |

93.4% line coverage, 96.4% method coverage. Run `./scripts/coverage.sh` for a full report.

## Documentation

| Document | Contents |
|----------|----------|
| [docs/cli-builder-spec.md](docs/cli-builder-spec.md) | Specification — interfaces, metadata model, config schema, test strategy |
| [docs/FUTURE.md](docs/FUTURE.md) | Roadmap — prioritized next steps |
| [docs/ADR.md](docs/ADR.md) | Architecture Decision Records (ADR-001 through ADR-015) |
| [docs/design-notes.md](docs/design-notes.md) | Edge-case policies, behavioral rules, diagnostic codes |
| [AGENTS.md](AGENTS.md) | Quick-start context for AI agents and contributors |
| [CHANGELOG.md](CHANGELOG.md) | Version history |

## License

Licensed under the [European Union Public Licence v. 1.2](LICENSE) (EUPL-1.2).

SPDX-License-Identifier: `EUPL-1.2`
