# cli-builder

Generate agent-ready CLIs directly from SDK type information.

## Problem

AI agents work best with CLI tools — structured output, discoverable commands, composable via pipes. But most SDKs ship without CLIs. Building a CLI by hand for each SDK is tedious, repetitive, and falls out of sync as SDKs evolve.

cli-builder eliminates the manual step: point it at an SDK assembly, get a fully functional CLI back.

## How it works

```
SDK Assembly (.dll)  ──▶  cli-builder  ──▶  Standalone CLI Project
```

```bash
# Generate a CLI from the OpenAI .NET SDK
cli-builder generate --assembly OpenAI.dll

# The output is a compilable C# project — no cli-builder dependency
cd openai-cli/
dotnet build
./openai-cli model list --json
```

The generated CLI follows agent-friendly patterns:

```bash
openai-cli model list --json          # structured output
openai-cli model list                 # human-readable table
openai-cli --help                     # lists all resources
openai-cli model --help               # lists all operations
openai-cli model list --help          # lists all parameters
```

## Documentation

| Document | Contents |
|----------|----------|
| [cli-builder-spec.md](cli-builder-spec.md) | Full specification — interfaces, metadata model, config schema, test strategy, scope |
| [docs/ADR.md](docs/ADR.md) | Architecture Decision Records (ADR-001 through ADR-015) |
| [docs/design-notes.md](docs/design-notes.md) | Edge-case policies, behavioral rules, diagnostic codes, test SDK manifest |
| [FUTURE.md](FUTURE.md) | Out-of-scope ideas and deferred features |
| [AGENTS.md](AGENTS.md) | Quick-start context for AI agents and contributors |
| [docs/process.md](docs/process.md) | Development methodology — how this project is built |
| `docs/internal/` | Agent implementation plans (step-by-step build instructions) |

## Status

Early development — spec and architectural decisions complete, implementation starting. See [First Actions](cli-builder-spec.md#first-actions) in the spec.

## License

Licensed under the [European Union Public Licence v. 1.2](LICENSE) (EUPL-1.2).

SPDX-License-Identifier: `EUPL-1.2`
