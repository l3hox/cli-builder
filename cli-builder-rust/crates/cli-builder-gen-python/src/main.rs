use std::path::PathBuf;
use std::process;

use clap::Parser;
use cli_builder_core::model_mapper::{self, MapperOptions};
use cli_builder_core::models::AdapterResultEnvelope;
use cli_builder_gen_python::python_mapper::PythonProfile;
use cli_builder_gen_python::renderer;

#[derive(Parser)]
#[command(name = "cli-builder-gen-python", about = "Generate a click-based Python CLI from SdkMetadata JSON")]
struct Args {
    /// Path to SdkMetadata JSON file
    #[arg(long)]
    input: PathBuf,

    /// Output directory for the generated project
    #[arg(long, default_value = "./output")]
    output: PathBuf,

    /// CLI name (derived from SDK name if omitted)
    #[arg(long)]
    cli_name: Option<String>,
}

fn main() {
    let args = Args::parse();

    // Read and parse metadata
    let json = std::fs::read_to_string(&args.input).unwrap_or_else(|e| {
        eprintln!("Failed to read {}: {}", args.input.display(), e);
        process::exit(1);
    });
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap_or_else(|e| {
        eprintln!("Failed to parse metadata JSON: {}", e);
        process::exit(1);
    });

    // Map to generator model
    let profile = PythonProfile;
    let options = MapperOptions {
        cli_name: args.cli_name,
    };
    let (model, diagnostics) = model_mapper::build(&envelope.metadata, &options, &profile);

    for d in &diagnostics {
        eprintln!("[{:?}] {}: {}", d.severity, d.code, d.message);
    }

    if let Err(e) = renderer::generate(&model, &args.output) {
        eprintln!("Generation failed: {}", e);
        process::exit(1);
    }

    eprintln!(
        "Generated Python CLI '{}' with {} resources at {}",
        model.cli_name,
        model.resources.len(),
        args.output.display()
    );
}
