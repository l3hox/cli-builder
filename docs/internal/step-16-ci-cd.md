# Step 16 — CI/CD Phase 1: GitHub Actions + local reproducibility

**Status**: Plan, council-reviewed and updated (Rounds 1 + 2 converged)
**Date**: 2026-04-15

## Goal

Stand up a CI gate that runs all 670 tests across Rust, .NET, and Python
on every PR and push to main, with matrix-OS coverage including Windows.
Provide a local target that reproduces CI failures offline. All OS jobs
are **required** checks — cross-platform is first-class per ADR-011.

**Explicit non-goals for Phase 1**:
- No release/publish workflow (Phase 3, deferred)
- No real-SDK integration tests against Stripe/OpenAI (may add via label-gate later)
- No Docker image
- No cross-language end-to-end orchestrator test (Phase 2)

## Decisions (post-council)

### 1. Local reproducibility: `Makefile`
**Council consensus — Makefile stands.** Ops made the decisive argument:
all three GitHub-hosted runner images (ubuntu, macos, windows-with-git-bash)
ship with `make`. `just` would add a bootstrap step to pin, version, and
audit on CI. Local dev can add `just` later as a convenience wrapper if
Windows `make` proves painful.

Targets:
- `make ci` — runs everything CI runs (test-rust + test-dotnet + test-python)
- `make test-rust` — `cargo test --workspace` in `crates/`
- `make test-dotnet` — `dotnet test` at repo root
- `make test-python` — `pytest` in `python/`
- `make build` — release build of the Rust orchestrator
- `make fmt` — cargo fmt + dotnet format (optional, best-effort)
- `make clean` — remove target/, bin/, obj/, __pycache__

### 2. Matrix OS: ubuntu-latest, macos-latest, windows-latest — ALL REQUIRED
User decision: every OS is a required check from day one. The matrix
exists to surface platform bugs; hiding Windows failures behind "advisory"
would defeat the purpose.

### 3. Windows integration-test strategy: Rust-native binary fixtures
**Council consensus — replace shell scripts with a Rust fixture crate.**

Current state (`crates/cli/test_fixtures/*.sh`):
5 shell scripts used by 11 integration tests in
`crates/cli/tests/integration.rs`. These scripts
emit canned JSON on stdout and exit with specific codes.

New design: a `crates/mock-adapter/` binary crate with
subcommands `ok`, `degraded`, `fail`, `bad-json`, `empty`. The integration
tests set `CLI_BUILDER_*_ADAPTER` env vars to point at the compiled
`mock-adapter` binary (picked up automatically by cargo's `CARGO_BIN_EXE_*`
mechanism). Same JSON payloads, same exit codes, but cross-platform.

Benefit:
- 11 tests run on all three OSes (no `#[cfg(unix)]` skip, no runtime guard)
- Shell scripts get deleted
- `.exe` suffix handling on Windows is exercised — catches the orchestrator
  bug class QA flagged (arg quoting, PATH, `.exe` resolution)

**This is a scope expansion vs the original plan** but closes a HIGH-risk
coverage gap identified by QA and aligns with ADR-011's cross-platform
requirement.

### 4. Git attributes: `.gitattributes` for line endings
Unchanged from Round 1 plan. `* text=auto eol=lf` covers `.snap`, `.tera`,
`.rs`, `.cs`, `.py`, `.json`. Note to self: snapshot regeneration
(`cargo insta review`) must happen on Linux/macOS or after `.gitattributes`
is in place, otherwise the `source:` header line in `.snap` files could
pick up backslashes.

### 5. Python version matrix: 3.10, 3.11, 3.12
Unchanged.

### 6. Rust version: pin now via `rust-toolchain.toml`
**Council correction — pin at implementation, not later.** Dev was right:
matrix CI without a pinned toolchain chases a moving stable, and each OS
runner upgrades on its own schedule. Add at repo root or under `crates/`:

```toml
[toolchain]
channel = "stable"
components = ["rustfmt", "clippy"]
```

(Pin to specific version like `"1.87.0"` once we confirm what current stable is.)

### 7. .NET version
Already pinned to 8.0.400 via `global.json`. `actions/setup-dotnet@v4`
reads this automatically.

### 8. Caching
- Rust: `Swatinem/rust-cache@v2`
- .NET: `actions/cache@v4` on `~/.nuget/packages` keyed by `**/*.csproj`
- Python: `actions/setup-python@v5` with `cache: 'pip'`

### 9. Workflow hardening (all from Ops findings)
- `permissions: read-all` at workflow level (restrict default GITHUB_TOKEN)
- `concurrency:` group with `cancel-in-progress: true` (kill superseded PR runs)
- `fail-fast: false` on every matrix (collect all failures, don't bail on first)
- `timeout-minutes:` per job — 20 ubuntu, 30 macos, 45 windows
- Pin action versions — use tags for now, rely on Dependabot to bump

### 10. Dependabot
Add `.github/dependabot.yml` with `github-actions` ecosystem for automatic
PRs when Actions get new versions.

### 11. CODEOWNERS
Add `.github/CODEOWNERS` with:
```
/.github/ @jlehotsky
```
So workflow changes require explicit owner review.

### 12. Hazard C (Python path separators) — closed
Dev verified `python/tests/test_integration.py` already
uses `pathlib.Path`. No code changes needed. Remove from open items.

### 13. Phase 1 green-bar meaning — explicit disclaimer
QA's signal-quality concern: a green Phase 1 bar means **unit and
per-language integration tests pass on all three OSes**. It does NOT mean:
- The orchestrator successfully invokes the .NET/Python adapters end-to-end
  (Phase 2 cross-language integration)
- The generated CLIs build against real SDK packages (deferred)
- The binary is releasable (Phase 3 release workflow)

Document this in the workflow file header comment so contributors
understand the gate's scope.

## Files to create

1. `.github/workflows/ci.yml` — main workflow (3 jobs × matrix OS)
2. `.github/dependabot.yml` — github-actions ecosystem
3. `.github/CODEOWNERS` — one-liner for `.github/`
4. `Makefile` — local CI target
5. `.gitattributes` — line-ending normalization
6. `rust-toolchain.toml` — pin Rust version
7. `crates/mock-adapter/` — new binary crate
   - `Cargo.toml`
   - `src/main.rs` with subcommands `ok`, `degraded`, `fail`, `bad-json`, `empty`
8. Delete: `crates/cli/test_fixtures/*.sh`
9. Update: `crates/cli/tests/integration.rs` to
   resolve the `mock-adapter` binary via `env!("CARGO_BIN_EXE_mock-adapter")`
   (or the workspace equivalent)
10. Update: `crates/Cargo.toml` workspace members list

## Workflow structure (target shape)

```yaml
name: CI
on:
  pull_request:
  push:
    branches: [main]

permissions: read-all

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

jobs:
  rust:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, macos-latest, windows-latest]
    runs-on: ${{ matrix.os }}
    timeout-minutes: 45
    defaults: { run: { working-directory: crates } }
    steps:
      - uses: actions/checkout@v4
      - uses: Swatinem/rust-cache@v2
      - run: cargo test --workspace --all-features

  dotnet:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, macos-latest, windows-latest]
    runs-on: ${{ matrix.os }}
    timeout-minutes: 30
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: nuget-${{ runner.os }}-${{ hashFiles('**/*.csproj') }}
      - run: dotnet test --configuration Release

  python:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, macos-latest, windows-latest]
        python: ['3.10', '3.11', '3.12']
    runs-on: ${{ matrix.os }}
    timeout-minutes: 20
    defaults: { run: { working-directory: python } }
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-python@v5
        with:
          python-version: ${{ matrix.python }}
          cache: pip
      - run: pip install -e ".[test]"
      - run: pytest
```

**Matrix size**: 3 (rust) + 3 (dotnet) + 9 (python) = **15 jobs, all required**.

## Success criteria

- All 15 jobs green on a fresh PR from main
- `make ci` reproduces the same test outcome locally on Linux
- No `#[cfg(unix)]` or runtime-guard test skips — the mock-adapter crate
  makes all tests cross-platform
- CI runtime under 15 minutes wall-clock for warm cache
- Branch protection configured to require all 15 checks post-merge

## Implementation order

1. Create `mock-adapter` crate; migrate integration tests; delete `.sh` fixtures;
   verify `cargo test --workspace` still passes locally on Linux
2. Add `rust-toolchain.toml`, `.gitattributes`
3. Write `Makefile`; verify `make ci` passes locally
4. Write `.github/workflows/ci.yml`, `.github/dependabot.yml`,
   `.github/CODEOWNERS`
5. Push branch, open PR, watch CI. Iterate until all 15 green.
6. Post-merge: configure branch protection to require all 15 checks.

## Open follow-ups (out of scope for Phase 1)

- Phase 2: cross-language end-to-end test (orchestrator + both adapters + both generators)
- Phase 3: release workflow on tag push (multi-arch Rust binaries)
- `cargo clippy -- -D warnings` as a separate lint job
- Coverage reporting
- Real-SDK integration tests (Stripe, OpenAI) behind a manual label gate
- `pip-compile` lockfile for Python to prevent transitive-dep drift
