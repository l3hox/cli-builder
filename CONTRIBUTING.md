# Contributing

cli-builder is a polyglot repo — Rust (orchestrator + generators), .NET (adapter + tests), Python (adapter). Tests run in all three, with a 15-job CI matrix across Windows, macOS, and Linux.

## Prerequisites

- [Rust (stable)](https://rustup.rs/) — `cargo`, toolchain pinned via `crates/rust-toolchain.toml`
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Python 3.10+ with `python3-venv` available (Debian/Ubuntu ship these as separate apt packages)

## Running tests

From the repo root:

```bash
make ci                  # all three languages
make test-rust           # cargo test --workspace
make test-dotnet         # dotnet build + dotnet test
make test-python         # pip install -e '.[test]' + pytest
make test-e2e-python     # PYTHONPATH-based runtime anchor (requires venv + click)
```

`make ci` must pass before opening a PR. If it's green locally, all 15 CI jobs will pass too — barring genuine cross-platform issues.

## Repo layout

```
crates/                 # Rust workspace — orchestrator + generators + shared core
  cli/                  # cli-builder binary (the orchestrator)
  core/                 # ModelMapper, ParameterFlattener, IdentifierValidator
  gen-python/           # Python CLI generator (click + Tera templates)
  gen-csharp/           # C# CLI generator (System.CommandLine + Tera templates)
  mock-adapter/         # Test fixture binary for integration tests
dotnet/                 # .NET adapter + legacy C# generator + tests
python/                 # Python adapter (standalone pip-installable package)
tests/fixtures/         # Shared SdkMetadata JSON fixtures (Rust + .NET both use them)
docs/                   # Spec, ADRs, design notes, internal step plans
```

Why this layout: [ADR-018](docs/ADR.md#adr-018-polyglot-repo-layout--crates--dotnet--python-at-root).

## Documentation hierarchy

Every piece of information should exist in exactly one place at the right granularity:

| Level | Document | When to edit |
|-------|----------|--------------|
| **Contract** | `docs/cli-builder-spec.md` | Changing the public interface, metadata model, config schema, or scope |
| **Decisions** | `docs/ADR.md` | Adding or superseding an architectural decision (full Nygard format) |
| **Behavioral rules** | `docs/design-notes.md` | Edge-case policies, sanitization surfaces, generator conventions, diagnostic codes |
| **Process** | `docs/process.md` | Changing the development methodology |
| **Plans** | `docs/internal/step-*.md` | Per-step implementation plans (archival once the step ships) |
| **Roadmap** | `docs/FUTURE.md` | Moving work between `Next up` / `Later` / `Completed` |

If you find yourself writing the same thing in two places at different levels, fix the placement before committing. Stale plans are fine to keep as history; stale specs are not.

## Workflow for non-trivial changes

1. **Step plan first** for features that take more than a single commit. Drop it under `docs/internal/step-NN-<topic>.md`. Templates exist (see `step-13-rust-python-generator.md` for a representative example).
2. **Council review before implementing** for anything touching architecture, public contracts, or cross-language boundaries. Invoke `/DeveloperCouncil` in Claude Code — a 3-round debate between Dev, QA, Ops, and (when relevant) DocumentationWriter / SecurityArchitect specialists. Apply the convergent verdict; skip the council for trivial changes.
3. **One auditable diff per commit.** Template refactors in one commit (snapshot-proven byte-identical), behavior changes in another (with test updates). Makes bisect and review tractable.
4. **Snapshot-based regression gates**. Both Rust (`insta` crate) and .NET (`tests/golden/`) have golden-file tests. Update snapshots intentionally, not reflexively — a snapshot diff is a contract change.
5. **ADR for decisions** with consequences beyond the immediate work (new language dependency, process boundary, breaking change).

## Tests must pass before merge

- All 15 CI jobs must be green.
- No `#[ignore]`'d tests without a tracking entry in `docs/FUTURE.md` — the CI has a `grep -q` step that fails if the link breaks. Orphaned ignore blocks rot silently and we don't tolerate them.
- Cross-platform regressions (Windows cp1252 encoding, macOS file locks, Windows path separators) are fair game — the matrix exists to catch them.

## Commit messages

Describe the **why**, not just the **what**. One-line summary under 72 chars, blank line, then context as needed.

Council fixes should state which specialist flagged what:

```
Fix optional-bool kwargs overwrite in Python generator

Per council review (Round 2, Dev + QA converged): optional bools rendered
as `is_flag=True, default=False` override SDK defaults because click
delivers False for omitted flags and False is not None. Switched to
`type=click.BOOL, default=None` tri-state.
```

## Reporting issues

File at <https://github.com/l3hox/cli-builder/issues>. For security-relevant issues, see [SECURITY.md](SECURITY.md).
