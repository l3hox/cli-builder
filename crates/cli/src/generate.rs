//! Generate command — orchestrates adapter → generator pipeline.

use std::path::Path;

use cli_builder_core::model_mapper::{self, MapperOptions};
use cli_builder_core::models::Diagnostic;

use crate::adapter::{self, AdapterError, AdapterKind};
use crate::diagnostics;

/// Generator target language.
#[derive(Debug, Clone, Copy)]
pub enum GeneratorTarget {
    CSharp,
    Python,
}

/// Options for the generate command.
pub struct GenerateOptions {
    pub adapter: AdapterKind,
    pub generator: GeneratorTarget,
    pub output_dir: String,
    pub cli_name: Option<String>,
    pub overwrite: bool,
    pub sdk_project_path: Option<String>,
}

/// Run the generate command. Returns exit code.
pub fn run(opts: GenerateOptions) -> i32 {
    // 1. Invoke adapter
    let adapter_output = match adapter::invoke(&opts.adapter) {
        Ok(output) => output,
        Err(AdapterError::EnvironmentFailure { stderr, diagnostics }) => {
            diagnostics::print_diagnostics(&diagnostics);
            if !stderr.is_empty() {
                eprintln!("{}", stderr.trim());
            }
            eprintln!("Adapter environment failure — cannot proceed.");
            return 2;
        }
        Err(e) => {
            eprintln!("{}", e);
            return 1;
        }
    };

    let envelope = adapter_output.envelope;
    let mut all_diagnostics: Vec<Diagnostic> = envelope.diagnostics;

    // 2. Map metadata to generator model using the appropriate profile
    let mapper_opts = MapperOptions {
        cli_name: opts.cli_name,
    };

    let output_path = Path::new(&opts.output_dir);

    // Check overwrite
    if output_path.exists() && !opts.overwrite {
        eprintln!(
            "Output directory '{}' already exists. Use --overwrite to replace.",
            opts.output_dir
        );
        return 1;
    }

    // 3. Generate based on target
    let result = match opts.generator {
        GeneratorTarget::Python => generate_python(&envelope.metadata, &mapper_opts, output_path, &mut all_diagnostics),
        GeneratorTarget::CSharp => generate_csharp(&envelope.metadata, &mapper_opts, output_path, &mut all_diagnostics, opts.sdk_project_path.as_deref()),
    };

    // 4. Print all diagnostics
    diagnostics::print_diagnostics(&all_diagnostics);

    match result {
        Ok(()) => {
            let has_errors = all_diagnostics.iter().any(|d| {
                matches!(d.severity, cli_builder_core::models::DiagnosticSeverity::Error)
            });
            if has_errors { 1 } else { 0 }
        }
        Err(e) => {
            eprintln!("Generation failed: {}", e);
            1
        }
    }
}

fn generate_python(
    metadata: &cli_builder_core::models::SdkMetadata,
    mapper_opts: &MapperOptions,
    output_path: &Path,
    all_diagnostics: &mut Vec<Diagnostic>,
) -> Result<(), Box<dyn std::error::Error>> {
    use cli_builder_gen_python::python_mapper::PythonProfile;

    let profile = PythonProfile;
    let (model, map_diags) = model_mapper::build(metadata, mapper_opts, &profile);
    all_diagnostics.extend(map_diags);

    cli_builder_gen_python::renderer::generate(&model, output_path)?;

    eprintln!(
        "Generated Python CLI '{}' with {} resources at {}",
        model.cli_name,
        model.resources.len(),
        output_path.display()
    );
    Ok(())
}

fn generate_csharp(
    metadata: &cli_builder_core::models::SdkMetadata,
    mapper_opts: &MapperOptions,
    output_path: &Path,
    all_diagnostics: &mut Vec<Diagnostic>,
    sdk_project_path: Option<&str>,
) -> Result<(), Box<dyn std::error::Error>> {
    use cli_builder_gen_csharp::csharp_mapper::CSharpProfile;
    use cli_builder_gen_csharp::csharp_model;

    let profile = CSharpProfile;
    let (model, map_diags) = model_mapper::build(metadata, mapper_opts, &profile);
    all_diagnostics.extend(map_diags);

    let mut gen_diags = Vec::new();
    let mut csharp_model = csharp_model::build_csharp_model(&model, &mut gen_diags);
    all_diagnostics.extend(gen_diags);

    if let Some(path) = sdk_project_path {
        csharp_model.sdk_project_path = Some(path.to_string());
    }

    cli_builder_gen_csharp::renderer::generate(&csharp_model, output_path)?;

    eprintln!(
        "Generated C# CLI '{}' with {} resources at {}",
        csharp_model.cli_name,
        csharp_model.resources.len(),
        output_path.display()
    );
    Ok(())
}
