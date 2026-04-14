//! Inspect command — invoke adapter and display metadata summary or raw JSON.

use crate::adapter::{self, AdapterError, AdapterKind};
use crate::diagnostics;

/// Options for the inspect command.
pub struct InspectOptions {
    pub adapter: AdapterKind,
    pub json: bool,
}

/// Run the inspect command. Returns exit code.
pub fn run(opts: InspectOptions) -> i32 {
    let adapter_output = match adapter::invoke(&opts.adapter) {
        Ok(output) => output,
        Err(AdapterError::EnvironmentFailure { stderr, diagnostics }) => {
            diagnostics::print_diagnostics(&diagnostics);
            if !stderr.is_empty() {
                eprintln!("{}", stderr.trim());
            }
            return 2;
        }
        Err(e) => {
            eprintln!("{}", e);
            return 1;
        }
    };

    let envelope = adapter_output.envelope;

    if opts.json {
        // Pass through raw JSON to stdout
        let json = serde_json::to_string_pretty(&envelope).unwrap();
        println!("{}", json);
    } else {
        // Human-readable summary
        let meta = &envelope.metadata;
        println!("SDK: {} v{}", meta.name, meta.version);
        println!("Resources: {}", meta.resources.len());

        if !meta.auth_patterns.is_empty() {
            let auth = &meta.auth_patterns[0];
            println!("Auth: {:?} (env: {})", auth.auth_type, auth.env_var);
        } else {
            println!("Auth: none detected");
        }

        if let Some(ref sa) = meta.static_auth {
            println!("Static auth: {}.{}", sa.type_name, sa.property_name);
        }

        println!();
        for resource in &meta.resources {
            println!(
                "  {} ({} operations)",
                resource.name,
                resource.operations.len()
            );
        }
    }

    // Print diagnostics to stderr
    diagnostics::print_diagnostics(&envelope.diagnostics);

    let has_errors = envelope.diagnostics.iter().any(|d| {
        matches!(d.severity, cli_builder_core::models::DiagnosticSeverity::Error)
    });
    if has_errors { 1 } else { 0 }
}
