//! Adapter subprocess invocation — spawns native adapters and captures SdkMetadata JSON.

use std::io;
use std::process::Command;

use cli_builder_core::models::{AdapterResultEnvelope, Diagnostic};

/// Which adapter to invoke.
#[derive(Debug, Clone)]
pub enum AdapterKind {
    DotNet { assembly: String },
    Python { package: String, module: Option<String> },
}

/// Result of invoking an adapter subprocess.
pub struct AdapterOutput {
    pub envelope: AdapterResultEnvelope,
    pub stderr_output: String,
}

/// Error from adapter invocation.
#[derive(Debug)]
pub enum AdapterError {
    /// Adapter binary not found
    NotFound(String),
    /// Adapter process failed to start
    SpawnFailed(io::Error),
    /// Adapter exited with code 2 (environment failure)
    EnvironmentFailure { stderr: String, diagnostics: Vec<Diagnostic> },
    /// Adapter produced invalid/empty JSON
    InvalidOutput(String),
    /// Adapter timed out
    Timeout,
}

impl std::fmt::Display for AdapterError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::NotFound(msg) => write!(f, "Adapter not found: {}", msg),
            Self::SpawnFailed(e) => write!(f, "Failed to start adapter: {}", e),
            Self::EnvironmentFailure { stderr, .. } => write!(f, "Adapter environment failure: {}", stderr),
            Self::InvalidOutput(msg) => write!(f, "Adapter produced invalid output: {}", msg),
            Self::Timeout => write!(f, "Adapter timed out"),
        }
    }
}

/// Invoke an adapter subprocess and return the parsed envelope.
pub fn invoke(kind: &AdapterKind) -> Result<AdapterOutput, AdapterError> {
    let (program, args) = build_command(kind);

    let output = Command::new(&program)
        .args(&args)
        .output()
        .map_err(|e| {
            if e.kind() == io::ErrorKind::NotFound {
                AdapterError::NotFound(format!(
                    "'{}' not found on PATH. Install the adapter or set {} environment variable.",
                    program,
                    match kind {
                        AdapterKind::DotNet { .. } => "CLI_BUILDER_DOTNET_ADAPTER",
                        AdapterKind::Python { .. } => "CLI_BUILDER_PYTHON_ADAPTER",
                    }
                ))
            } else {
                AdapterError::SpawnFailed(e)
            }
        })?;

    let stderr = String::from_utf8_lossy(&output.stderr).to_string();
    let stdout = String::from_utf8_lossy(&output.stdout).to_string();

    // Exit code 2: environment failure — abort
    if output.status.code() == Some(2) {
        // Try to parse diagnostics from stdout even on failure
        let diagnostics = serde_json::from_str::<AdapterResultEnvelope>(&stdout)
            .map(|e| e.diagnostics)
            .unwrap_or_default();
        return Err(AdapterError::EnvironmentFailure { stderr, diagnostics });
    }

    // Empty stdout
    if stdout.trim().is_empty() {
        return Err(AdapterError::InvalidOutput(
            "Adapter produced empty output (no JSON on stdout)".into(),
        ));
    }

    // Parse JSON
    let envelope: AdapterResultEnvelope = serde_json::from_str(&stdout)
        .map_err(|e| AdapterError::InvalidOutput(format!("Failed to parse adapter JSON: {}", e)))?;

    Ok(AdapterOutput {
        envelope,
        stderr_output: stderr,
    })
}

/// Build the command + args for an adapter invocation.
fn build_command(kind: &AdapterKind) -> (String, Vec<String>) {
    match kind {
        AdapterKind::DotNet { assembly } => {
            // Check env-var override first
            let program = std::env::var("CLI_BUILDER_DOTNET_ADAPTER")
                .unwrap_or_else(|_| "cli-builder-dotnet".to_string());
            let args = vec![
                "inspect".to_string(),
                "--assembly".to_string(),
                assembly.clone(),
                "--json".to_string(),
            ];
            (program, args)
        }
        AdapterKind::Python { package, module } => {
            let program = std::env::var("CLI_BUILDER_PYTHON_ADAPTER")
                .unwrap_or_else(|_| "python3".to_string());
            let mut args = vec![
                "-m".to_string(),
                "cli_builder_adapter".to_string(),
                "--package".to_string(),
                package.clone(),
                "--json".to_string(),
            ];
            if let Some(m) = module {
                args.push("--module".to_string());
                args.push(m.clone());
            }
            (program, args)
        }
    }
}
