//! End-to-end runtime tests for the generated Python CLI.
//!
//! Two tests live here:
//!
//!   1. `help_output_snapshot` (active) — PYTHONPATH-based runtime anchor.
//!      Generates a CLI from the TestSdk fixture, spawns
//!      `python -m testsdk_cli --help`, snapshots the stdout. Catches click
//!      semantic drift and generated-CLI import regressions. Skips gracefully
//!      if python or click is unavailable.
//!
//!   2. `console_script_entry_point_end_to_end` (#[ignore]'d) — placeholder
//!      for the heavier venv+pip+console-script E2E. Tracking entry in
//!      `docs/FUTURE.md`. Runs the `[project.scripts]` entry point, which
//!      PYTHONPATH invocation bypasses.

use std::path::Path;

use cli_builder_core::model_mapper::{self, MapperOptions};
use cli_builder_core::models::AdapterResultEnvelope;
use cli_builder_core::test_support;
use cli_builder_gen_python::python_mapper::PythonProfile;
use cli_builder_gen_python::renderer;

/// Materialize the TestSdk-based Python CLI project into `output_dir`.
fn generate_testsdk(output_dir: &Path) {
    let fixture = test_support::fixtures_dir().join("testsdk-metadata.json");
    let json = std::fs::read_to_string(&fixture)
        .unwrap_or_else(|e| panic!("failed to read {}: {e}", fixture.display()));
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap();
    let (model, _) = model_mapper::build(
        &envelope.metadata,
        &MapperOptions {
            cli_name: Some("testsdk-cli".into()),
        },
        &PythonProfile,
    );
    renderer::generate(&model, output_dir).unwrap();
}

/// Runtime anchor: spawn `python -m testsdk_cli --help` against the generated
/// CLI and snapshot the stdout. Catches click semantic drift (e.g. 8→9) that
/// pure string scans can miss. Uses PYTHONPATH — no pip install / venv needed.
///
/// CI pins `click==8.*` and installs it in the rust job, so this test runs
/// for real on all three OSes. Local dev without click still passes via the
/// graceful skip below.
#[test]
fn help_output_snapshot() {
    let python = if cfg!(windows) { "python" } else { "python3" };

    // Probe: if python or click isn't available locally, skip rather than
    // hard-panic. CI guarantees both, so a skip in CI would be a bug.
    match std::process::Command::new(python)
        .args(["-c", "import click"])
        .output()
    {
        Err(_) => {
            eprintln!("help_output_snapshot: `{}` not in PATH — skipping", python);
            return;
        }
        Ok(out) if !out.status.success() => {
            eprintln!(
                "help_output_snapshot: `{}` has no `click` module — skipping",
                python
            );
            return;
        }
        _ => {}
    }

    let dir = tempfile::tempdir().unwrap();
    generate_testsdk(dir.path());
    let src_dir = dir.path().join("src");
    let output = std::process::Command::new(python)
        .env("PYTHONPATH", &src_dir)
        .args(["-m", "testsdk_cli", "--help"])
        .output()
        .expect("Failed to invoke python despite probe success");

    assert!(
        output.status.success(),
        "python -m testsdk_cli --help failed (exit {:?}):\nstderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stderr)
    );

    let stdout = String::from_utf8_lossy(&output.stdout).to_string();
    insta::assert_snapshot!("help_output", stdout);
}

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
/// Step 5 is the specific value this test adds over [`help_output_snapshot`]:
/// it exercises the installed console-script entry point. The `python -m`
/// invocation in `help_output_snapshot` bypasses that entry point.
///
/// Tracking entry in `docs/FUTURE.md` under "Other → Full venv+pip
/// console-script E2E". The CI `grep -q` step in `.github/workflows/ci.yml`
/// enforces that the tracking entry still exists; if it disappears, CI
/// fails and this test should be deleted.
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
