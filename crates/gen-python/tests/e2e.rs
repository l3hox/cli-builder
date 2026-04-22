//! End-to-end runtime tests for the generated Python CLI.
//!
//! The primary PYTHONPATH runtime anchor (`help_output_snapshot`) lives in
//! `src/tests.rs::template_rendering`. It runs as part of the main Rust test
//! suite and gates against click semantic drift, template output drift, and
//! Python syntax regressions — all via `python -m testsdk_cli --help`.
//!
//! This file hosts heavier integration tests that require more setup than
//! ambient Python + click. Currently a single `#[ignore]`'d placeholder; the
//! corresponding roadmap item lives in `docs/FUTURE.md` under "Other".

/// Full console-script end-to-end test. Not yet implemented.
///
/// Scope:
///   1. Create a fresh venv.
///   2. `pip install -e python/tests/test_sdk` (requires adding a minimal
///      `pyproject.toml` to that directory — blocker today).
///   3. Extract metadata via the Python adapter from the installed test_sdk.
///   4. Run the Rust generator → produce a Python CLI project.
///   5. `pip install -e <generated-dir>` — activates `[project.scripts]`.
///   6. Invoke `testsdk-cli customer get --id-value cust_123 --json`.
///   7. Assert exit 0 + JSON output shape.
///
/// Step 5 is the specific value this test adds over the existing PYTHONPATH
/// anchor: it exercises the installed console-script entry point. The
/// `python -m` invocation in `help_output_snapshot` bypasses that entry point.
///
/// Deferred per PR3 scope (2026-04-22). Tracking entry in `docs/FUTURE.md`
/// under "Other → Full venv+pip console-script E2E". Delete this test if the
/// tracking entry disappears without being implemented — an orphaned
/// `#[ignore]` rots silently.
#[test]
#[ignore = "pending venv+pip infra — see docs/FUTURE.md"]
fn console_script_entry_point_end_to_end() {
    unimplemented!(
        "See docs/FUTURE.md under 'Other → Full venv+pip console-script E2E'. \
         Requires: (a) python/tests/test_sdk/pyproject.toml, (b) venv creation \
         in a tempdir, (c) two pip install -e steps, (d) CLI invocation and \
         JSON assertion. Estimated 100 LOC of test setup."
    );
}
