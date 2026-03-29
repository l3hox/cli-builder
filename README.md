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

Validated against the **OpenAI .NET SDK 2.9.1** — 20 resources, 169 operations:

```bash
# Generated CLI output
$ openai-cli --help
Description:
  openai-cli -- CLI for OpenAI

Commands:
  chat            audio           assistant       batch
  embedding       evaluation      fine-tuning     image
  moderation      open-ai-model   vector-store    ...

$ openai-cli chat complete-chat --help
Options:
  --messages <messages> (REQUIRED)
  --response-modalities <Audio|Default|Text> (REQUIRED)
  --frequency-penalty <frequency-penalty>
  --temperature <temperature>
  --json-input <json-input>    Full input as JSON
  --json                       Output as JSON instead of table format
  --api-key <api-key>          API key (prefer OPENAI_APIKEY env var)

$ export OPENAI_APIKEY=sk-...
$ openai-cli chat complete-chat --messages "hello" --json
{
  "command": "chat complete-chat",
  "parameters": { "messages": "hello", ... },
  "authenticated": true
}
```

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

227 tests across 3 projects:

| Project | Tests | Covers |
|---------|-------|--------|
| Generator Tests | 170 | Template rendering, parameter flattening, model mapping, sanitization, golden files, compile verification |
| Core Tests | 43 | Adapter extraction, metadata serialization, type resolution |
| Integration Tests | 14 | OpenAI SDK extraction, OpenAI CLI compilation |

Code coverage: **80.6% line, 95.3% method**. Run `./scripts/coverage.sh` for a full report.

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

Steps 1-6 complete. The generator produces compilable, runnable CLIs from .NET SDK assemblies. Validated against the OpenAI .NET SDK at scale (20 resources, 169 operations).

**Remaining:** Step 7 — wire real SDK method calls in generated handlers (currently stubbed with parameter echo). See [First Actions](docs/cli-builder-spec.md#first-actions) in the spec.

## License

Licensed under the [European Union Public Licence v. 1.2](LICENSE) (EUPL-1.2).

SPDX-License-Identifier: `EUPL-1.2`
