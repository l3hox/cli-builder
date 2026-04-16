//! Cross-platform mock SDK adapter.
//!
//! Emits canned `AdapterResultEnvelope` JSON on stdout and exits with a
//! specific code, mirroring the real .NET and Python adapters. Used by
//! the cli-builder integration tests in place of shell-script fixtures,
//! so tests run on Linux, macOS, and Windows without `.cmd`/`.ps1` ports.
//!
//! Mode is selected via the `MOCK_ADAPTER_MODE` env var, one of
//! `ok` (default), `degraded`, `fail`, `bad-json`, `empty`. All CLI args
//! are accepted and ignored — the orchestrator passes through
//! `--package`, `--assembly`, `--json`, etc., which the real adapters
//! consume but the mock doesn't need.

use std::process::ExitCode;

const OK_JSON: &str = r#"{"schemaVersion":"1","metadata":{"name":"TestSdk","version":"1.0.0","resources":[{"name":"customer","description":"Customer resource","operations":[{"name":"get","description":"Get a customer","parameters":[{"name":"id","type":{"kind":"primitive","name":"str","isNullable":false,"isAbstract":false,"isExtensibleEnum":false},"required":true}],"returnType":{"kind":"class","name":"Customer","isNullable":false,"isAbstract":false,"isExtensibleEnum":false},"isStreaming":false}],"sourceClassName":"CustomerClient","sourceModule":"test_sdk.services","hasParameterlessCtor":false}],"authPatterns":[{"type":"apiKey","envVar":"TEST_API_KEY","parameterName":"api_key"}],"staticAuth":null},"diagnostics":[{"severity":"info","code":"CB601","message":"Package imported at runtime"}]}"#;

const DEGRADED_JSON: &str = r#"{"schemaVersion":"1","metadata":{"name":"TestSdk","version":"1.0.0","resources":[],"authPatterns":[],"staticAuth":null},"diagnostics":[{"severity":"error","code":"CB100","message":"Some types could not be extracted"}]}"#;

const FAIL_JSON: &str = r#"{"schemaVersion":"1","metadata":{"name":"","version":"0.0.0","resources":[],"authPatterns":[],"staticAuth":null},"diagnostics":[{"severity":"error","code":"CB600","message":"Could not import package"}]}"#;

const BAD_JSON: &str = r#"{"schemaVersion":"1","metadata":{"name":"Test"#;

fn main() -> ExitCode {
    let mode = std::env::var("MOCK_ADAPTER_MODE").unwrap_or_else(|_| "ok".to_string());

    match mode.as_str() {
        "ok" => {
            println!("{}", OK_JSON);
            ExitCode::from(0)
        }
        "degraded" => {
            println!("{}", DEGRADED_JSON);
            ExitCode::from(1)
        }
        "fail" => {
            println!("{}", FAIL_JSON);
            ExitCode::from(2)
        }
        "bad-json" => {
            println!("{}", BAD_JSON);
            ExitCode::from(0)
        }
        "empty" => ExitCode::from(0),
        other => {
            eprintln!("mock-adapter: unknown mode '{}'", other);
            ExitCode::from(64)
        }
    }
}
