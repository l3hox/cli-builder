# Step 13b: Python Generator Finalize — close deferred council items

**Prerequisite:** Step 13 complete (Python CLI generator in Rust, 26 tests, insta golden snapshots, ast.parse validation). Steps 14–16 complete (C# generator, orchestrator, CI/CD). Directory restructure done.
**Output:** Correctness bug fixed, template refactored for maintainability, end-to-end PYTHONPATH smoke test, stale memory updated.

---

## Problem

Three items deferred from the original Step 13 council remain open, surfaced in an assessment council on 2026-04-21:

1. **Correctness bug** — optional `is_flag=True, default=False` booleans in the generated CLI silently clobber SDK defaults. `False is not None` is True, so `kwargs["PreferredContact"] = False` is written whenever the user omits `--preferred-contact`. Evidence: `crates/gen-python/src/snapshots/cli_builder_gen_python__tests__customer_py.snap:51,104`.
2. **Template maintainability** — `crates/gen-python/templates/resource.py.tera:38` is one 300+ character line of six nested `{% if %}` conditionals. Hard to read, hard to edit.
3. **Coverage gap** — tests validate syntax (`ast.parse`) and structural stability (insta snapshots) but never install or invoke the generated CLI end-to-end. The `[project.scripts]` wiring is wholly untested.

Plus housekeeping:

4. (Optional) Generated output has runs of 2–3 blank lines from Tera control-tag newlines (`customer_py.snap:21–34`).
5. `step13_context.md` memory is stale — claims "Phase 2 next" when Step 13 is fully complete per `docs/FUTURE.md:54` and `docs/ADR.md:853`.

---

## Council verdict (2026-04-21, 3 rounds, full convergence)

| Decision | Reasoning |
|---|---|
| **Optional bool → `type=click.BOOL, default=None`** | `is_flag=True, flag_value=True, default=None` is undocumented in click. The paired `--flag/--no-flag` alternative doubles `--help` surface area (20 fields → 40 option lines). `type=click.BOOL` gives clean tri-state: absent=None, `--x true`/`--x false` explicit. |
| **Required bool stays `is_flag=True, default=False, required=True`** | Required means the user must pass something; flag ergonomics are fine. |
| **Regression test = string scan + `--help` subprocess** | Template is deterministic — rendered string is the proof. One `python -m testsdk_cli --help` exit-zero check anchors against click 8→9 semantic drift. No click kwargs capture needed. |
| **Inline `{% set %}` blocks, NOT a Tera macro** | Macros require `{% import %}` wiring in `renderer.rs` — coupling not worth it for a single template. |
| **PYTHONPATH, NOT venv+pip, for E2E** | `python/tests/test_sdk/` has no `pyproject.toml` — `pip install` fails at step 1. PYTHONPATH runs in ~1s vs ~30s, eliminates three cross-platform failure modes (network, pyproject packaging, Windows `Scripts/` vs `bin/`). |
| **Full venv+pip E2E deferred to nightly** | Keeps PR matrix under 2 min. Console-script `[project.scripts]` coverage is the only thing PYTHONPATH can't catch — gated `#[ignore]` with a ticket reference, deleted after 2 sprints if orphaned. |
| **`setup-python@v6` added to rust CI job** | Today it works by accident (ambient Python). Hard policy: declare the dependency. |
| **Synthetic `GeneratorModel` builder tests for coverage gaps** | Pattern already established at `tests.rs:310,353`. Cleaner than fixture edits — one diff type per commit. |
| **Item 1 before Item 2, separate PRs** | Each commit has one auditable diff type (bool fix vs pure refactor). Bisect-able. |

---

## Implementation Order

### PR 1 — Optional-bool fix + coverage

**1a. Template fix**

Modify `crates/gen-python/templates/resource.py.tera:38`, bool branch:

- Current: `{% if param.cli_type == "bool" %}, is_flag=True, default=False{% endif %}`
- New:
  - Optional bool: `, type=click.BOOL, default=None`
  - Required bool: `, is_flag=True, default=False` (the `required=True` clause already handles the rest)

Branch on `param.is_required` inside the bool case.

**1b. Regression tests in `crates/gen-python/src/tests.rs`**

1. String scan — assert no generated `.py` contains `is_flag=True, default=False` on a line without `required=True`. Catches the bug class, not just this instance.
2. New insta snapshot `golden_order_py` — locks `IsPriority`/`GiftWrap` (currently unprotected).
3. Synthetic `GeneratorModel` test for a **required bool** parameter — verify it renders as `is_flag=True, default=False, required=True`. Build inline using the pattern at `tests.rs:310,353`.
4. Synthetic `GeneratorModel` test for an **optional float** parameter — verify it renders as `type=float, default=None` (or equivalent). Float branch of line 38 is currently uncovered by any snapshot.

**1c. `--help` stdout snapshot (runtime anchor)**

New test in `crates/gen-python/src/tests.rs`:
- Generate TestSdk CLI
- Spawn `python -m testsdk_cli --help` with `PYTHONPATH=<tempdir>/src`
- Assert exit 0
- `insta::assert_snapshot!` the stdout — locks the `--help` surface against click semantic drift

**1d. Update existing snapshots**

Run `cargo insta accept` for `golden_customer_py` (preferred_contact line changes).

### PR 2 — Template refactor (inline `{% set %}` blocks)

Modify `crates/gen-python/templates/resource.py.tera:38`:

Replace the 300+ char single-line `@click.option(...)` with:
```jinja
{% set required_clause = ", required=True" if param.is_required else "" %}
{% set type_clause = ... %}
{% set choice_clause = ... %}
{% set help_clause = ", help=\"" ~ param.description ~ "\"" if param.description else "" %}
@click.option("--{{ param.cli_flag }}"{{ required_clause }}{{ type_clause }}{{ choice_clause }}{{ help_clause }})
```

**Proof of no behavior change**: every existing insta snapshot passes byte-for-byte. Any diff = refactor has a bug.

### PR 3 — CI infra + PYTHONPATH E2E test

**3a. CI setup-python**

`.github/workflows/ci.yml:20-36` — add before the `cargo test` step:

```yaml
      - uses: actions/setup-python@v6
        with:
          python-version: '3.12'
```

**3b. Makefile**

Add `test-e2e-python` target (parallel to existing `test-rust`, `test-dotnet`, `test-python`). Do not fold into `test-rust` — E2E requires Python in PATH, `test-rust` should not.

**3c. New integration test**

`crates/gen-python/tests/e2e.rs`:
- Generate TestSdk CLI to tempdir
- Spawn `python -m testsdk_cli --help` with `PYTHONPATH=<tempdir>/src`
- Assert exit 0 and stdout contains expected command groups
- Gate: `#[cfg_attr(not(target_os = "linux"), ignore)]` initially. Re-evaluate macOS/Windows once stable.

**3d. Console-script placeholder**

Second `#[ignore]` test in the same file with a `// TODO(issue-NN): requires venv+pip+pyproject.toml for test_sdk` comment, covering the `[project.scripts]` entry point that PYTHONPATH bypasses. Orphaned after 2 sprints without a ticket → delete.

### Optional item 4 — Whitespace cleanup

Separate small commit. Apply Tera `{%-` / `-%}` whitespace control to strip extra newlines in `resource.py.tera` and other templates. Accept snapshot diffs per-template after visual review.

### Item 5 — Memory update

Update `~/.claude/projects/-home-jlehotsky-prog-cli-builder/memory/step13_context.md` to reflect that Step 13 (and this follow-up) are complete. Fix the `MEMORY.md` index line that claims "Phase 2 next".

---

## Key files

| File | Change |
|------|--------|
| `crates/gen-python/templates/resource.py.tera` | Line 38: optional-bool branch change |
| `crates/gen-python/src/tests.rs` | Add string scan, golden_order_py, 2 synthetic model tests, --help snapshot |
| `crates/gen-python/src/snapshots/cli_builder_gen_python__tests__customer_py.snap` | Updated for preferred_contact |
| `crates/gen-python/src/snapshots/cli_builder_gen_python__tests__order_py.snap` | New |
| `crates/gen-python/src/snapshots/cli_builder_gen_python__tests__help_output.snap` | New |
| `crates/gen-python/tests/e2e.rs` | New (PR 3) |
| `.github/workflows/ci.yml` | Add setup-python to rust job |
| `Makefile` | Add `test-e2e-python` target |

---

## Verification

```bash
# PR 1
cd crates && cargo test --package cli-builder-gen-python    # 26 + 4 new = 30 tests

# PR 2 — must be zero-diff
cd crates && cargo test --package cli-builder-gen-python    # still 30, all pass

# PR 3
make test-e2e-python    # new target, Linux-only round 1
```

Full `make ci` green before each push.

---

## Risk

**Low.** PR 1 is template-local + contained tests. PR 2 is a pure refactor gated by byte-for-byte snapshots. PR 3 adds infra but PYTHONPATH sidesteps every cross-platform failure mode the council identified.

Residual risks:
- Click 9.x could change `type=click.BOOL, default=None` semantics — the `--help` snapshot anchor catches drift.
- `{% set %}` blocks could introduce whitespace noise — existing snapshots will fail, forcing a fix before merge.

---

## What this does NOT solve

- Full venv+pip+console-script E2E (deferred to nightly job, separate ticket)
- Windows/macOS enablement for the PYTHONPATH E2E (round 2 once Linux is stable)
- Any Python generator features not already scoped in Step 13
