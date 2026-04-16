//! Tests for the cli-builder orchestrator.
//!
//! These tests invoke the compiled `cli-builder` binary as a subprocess
//! and point it at the `mock-adapter` binary (a workspace crate that
//! emits canned JSON) via the `CLI_BUILDER_*_ADAPTER` env vars. Requires
//! the full workspace to be built — run `cargo test --workspace`.

use std::path::PathBuf;
use std::process::Command;

fn cli_binary() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_cli-builder"))
}

fn mock_adapter_binary() -> PathBuf {
    let mut path = cli_binary();
    path.set_file_name(format!("mock-adapter{}", std::env::consts::EXE_SUFFIX));
    path
}

/// Return a non-existing output path within a tempdir.
fn output_dir(dir: &tempfile::TempDir) -> String {
    dir.path().join("out").to_string_lossy().to_string()
}

// ================================================================
// Adapter invocation tests (via mock-adapter binary)
// ================================================================

#[test]
fn adapter_ok_returns_exit_0() {
    let dir = tempfile::tempdir().unwrap();
    let output = Command::new(cli_binary())
        .args([
            "generate",
            "--adapter", "python",
            "--package", "fake",
            "--generator", "python",
            "--output", &output_dir(&dir),
        ])
        .env("CLI_BUILDER_PYTHON_ADAPTER", &mock_adapter_binary())
        .env("MOCK_ADAPTER_MODE", "ok")
        .output()
        .expect("Failed to run cli-builder");

    assert!(
        output.status.success(),
        "Expected exit 0, got {:?}\nstderr: {}",
        output.status.code(),
        String::from_utf8_lossy(&output.stderr)
    );

    let py_files: Vec<_> = walkdir::WalkDir::new(dir.path().join("out"))
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.path().extension().map_or(false, |ext| ext == "py"))
        .collect();
    assert!(!py_files.is_empty(), "Expected .py files in output");
}

#[test]
fn adapter_degraded_exit_1_still_generates() {
    let dir = tempfile::tempdir().unwrap();
    let output = Command::new(cli_binary())
        .args([
            "generate",
            "--adapter", "python",
            "--package", "fake",
            "--generator", "python",
            "--output", &output_dir(&dir),
        ])
        .env("CLI_BUILDER_PYTHON_ADAPTER", &mock_adapter_binary())
        .env("MOCK_ADAPTER_MODE", "degraded")
        .output()
        .expect("Failed to run cli-builder");

    assert_eq!(output.status.code(), Some(1));
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("CB100"), "Should contain error diagnostic");
}

#[test]
fn adapter_fail_exit_2_aborts() {
    let dir = tempfile::tempdir().unwrap();
    let output = Command::new(cli_binary())
        .args([
            "generate",
            "--adapter", "python",
            "--package", "fake",
            "--generator", "python",
            "--output", &output_dir(&dir),
        ])
        .env("CLI_BUILDER_PYTHON_ADAPTER", &mock_adapter_binary())
        .env("MOCK_ADAPTER_MODE", "fail")
        .output()
        .expect("Failed to run cli-builder");

    assert_eq!(output.status.code(), Some(2));
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(stderr.contains("CB600"), "Should contain CB600 diagnostic, got: {}", stderr);
}

#[test]
fn adapter_bad_json_returns_error() {
    let dir = tempfile::tempdir().unwrap();
    let output = Command::new(cli_binary())
        .args([
            "generate",
            "--adapter", "python",
            "--package", "fake",
            "--generator", "python",
            "--output", &output_dir(&dir),
        ])
        .env("CLI_BUILDER_PYTHON_ADAPTER", &mock_adapter_binary())
        .env("MOCK_ADAPTER_MODE", "bad-json")
        .output()
        .expect("Failed to run cli-builder");

    assert!(!output.status.success());
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(
        stderr.contains("invalid") || stderr.contains("parse") || stderr.contains("Failed"),
        "Should report JSON parse error, got: {}",
        stderr
    );
}

#[test]
fn adapter_empty_stdout_returns_error() {
    let dir = tempfile::tempdir().unwrap();
    let output = Command::new(cli_binary())
        .args([
            "generate",
            "--adapter", "python",
            "--package", "fake",
            "--generator", "python",
            "--output", &output_dir(&dir),
        ])
        .env("CLI_BUILDER_PYTHON_ADAPTER", &mock_adapter_binary())
        .env("MOCK_ADAPTER_MODE", "empty")
        .output()
        .expect("Failed to run cli-builder");

    assert!(!output.status.success());
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(
        stderr.contains("empty") || stderr.contains("no JSON"),
        "Should report empty output, got: {}",
        stderr
    );
}

#[test]
fn adapter_not_found_returns_error() {
    let dir = tempfile::tempdir().unwrap();
    let nonexistent = if cfg!(windows) {
        "C:\\nonexistent\\binary.exe"
    } else {
        "/nonexistent/binary"
    };
    let output = Command::new(cli_binary())
        .args([
            "generate",
            "--adapter", "python",
            "--package", "fake",
            "--generator", "python",
            "--output", &output_dir(&dir),
        ])
        .env("CLI_BUILDER_PYTHON_ADAPTER", nonexistent)
        .output()
        .expect("Failed to run cli-builder");

    assert!(!output.status.success());
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(
        stderr.contains("not found") || stderr.contains("Not found") || stderr.contains("cannot find"),
        "Should report adapter not found, got: {}",
        stderr
    );
}

// ================================================================
// E2E: generate command with mock adapter
// ================================================================

#[test]
fn e2e_generate_python_cli() {
    let dir = tempfile::tempdir().unwrap();
    let output = Command::new(cli_binary())
        .args([
            "generate",
            "--adapter", "python",
            "--package", "fake",
            "--generator", "python",
            "--output", &output_dir(&dir),
            "--cli-name", "test-cli",
        ])
        .env("CLI_BUILDER_PYTHON_ADAPTER", &mock_adapter_binary())
        .env("MOCK_ADAPTER_MODE", "ok")
        .output()
        .expect("Failed to run cli-builder");

    assert!(output.status.success());

    assert!(dir.path().join("out/pyproject.toml").exists());
    assert!(dir.path().join("out/src/test_cli/cli.py").exists());
    assert!(dir.path().join("out/src/test_cli/commands/customer.py").exists());
}

#[test]
fn e2e_generate_csharp_cli() {
    let dir = tempfile::tempdir().unwrap();
    let output = Command::new(cli_binary())
        .args([
            "generate",
            "--adapter", "dotnet",
            "--assembly", "fake.dll",
            "--generator", "csharp",
            "--output", &output_dir(&dir),
            "--cli-name", "test-cli",
        ])
        .env("CLI_BUILDER_DOTNET_ADAPTER", &mock_adapter_binary())
        .env("MOCK_ADAPTER_MODE", "ok")
        .output()
        .expect("Failed to run cli-builder");

    assert!(output.status.success());

    assert!(dir.path().join("out/test-cli/test-cli.csproj").exists());
    assert!(dir.path().join("out/test-cli/Program.cs").exists());
    assert!(dir.path().join("out/test-cli/Commands/CustomerCommands.cs").exists());
}

// ================================================================
// Inspect command
// ================================================================

#[test]
fn inspect_json_passes_through() {
    let output = Command::new(cli_binary())
        .args([
            "inspect",
            "--adapter", "python",
            "--package", "fake",
            "--json",
        ])
        .env("CLI_BUILDER_PYTHON_ADAPTER", &mock_adapter_binary())
        .env("MOCK_ADAPTER_MODE", "ok")
        .output()
        .expect("Failed to run cli-builder");

    assert!(output.status.success());
    let stdout = String::from_utf8_lossy(&output.stdout);
    assert!(stdout.contains("\"schemaVersion\""));
    assert!(stdout.contains("\"TestSdk\""));
}

#[test]
fn inspect_summary_shows_resources() {
    let output = Command::new(cli_binary())
        .args([
            "inspect",
            "--adapter", "python",
            "--package", "fake",
        ])
        .env("CLI_BUILDER_PYTHON_ADAPTER", &mock_adapter_binary())
        .env("MOCK_ADAPTER_MODE", "ok")
        .output()
        .expect("Failed to run cli-builder");

    assert!(output.status.success());
    let stdout = String::from_utf8_lossy(&output.stdout);
    assert!(stdout.contains("TestSdk"));
    assert!(stdout.contains("Resources: 1"));
    assert!(stdout.contains("customer"));
}

// ================================================================
// Default generator selection
// ================================================================

#[test]
fn dotnet_adapter_defaults_to_csharp_generator() {
    let dir = tempfile::tempdir().unwrap();
    let output = Command::new(cli_binary())
        .args([
            "generate",
            "--adapter", "dotnet",
            "--assembly", "fake.dll",
            "--output", &output_dir(&dir),
            "--cli-name", "test-cli",
        ])
        .env("CLI_BUILDER_DOTNET_ADAPTER", &mock_adapter_binary())
        .env("MOCK_ADAPTER_MODE", "ok")
        .output()
        .expect("Failed to run cli-builder");

    assert!(output.status.success());
    assert!(dir.path().join("out/test-cli/test-cli.csproj").exists());
}
