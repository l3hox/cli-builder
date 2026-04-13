use std::path::PathBuf;
use std::process;

use cli_builder_core::model_mapper::{self, MapperOptions};
use cli_builder_core::models::AdapterResultEnvelope;
use cli_builder_gen_python::python_mapper::PythonProfile;
use cli_builder_gen_python::renderer;

fn main() {
    let args: Vec<String> = std::env::args().collect();

    if args.len() < 3 || args[1] != "--input" {
        eprintln!("Usage: cli-builder-gen-python --input <metadata.json> --output <dir> [--cli-name <name>]");
        process::exit(2);
    }

    let input_path = PathBuf::from(&args[2]);
    let mut output_dir = PathBuf::from("./output");
    let mut cli_name: Option<String> = None;

    let mut i = 3;
    while i < args.len() {
        match args[i].as_str() {
            "--output" if i + 1 < args.len() => {
                output_dir = PathBuf::from(&args[i + 1]);
                i += 2;
            }
            "--cli-name" if i + 1 < args.len() => {
                cli_name = Some(args[i + 1].clone());
                i += 2;
            }
            _ => {
                eprintln!("Unknown argument: {}", args[i]);
                process::exit(2);
            }
        }
    }

    // Read and parse metadata
    let json = std::fs::read_to_string(&input_path).unwrap_or_else(|e| {
        eprintln!("Failed to read {}: {}", input_path.display(), e);
        process::exit(1);
    });
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap_or_else(|e| {
        eprintln!("Failed to parse metadata JSON: {}", e);
        process::exit(1);
    });

    // Map to generator model
    let profile = PythonProfile;
    let options = MapperOptions { cli_name };
    let (model, diagnostics) = model_mapper::build(&envelope.metadata, &options, &profile);

    // Print diagnostics
    for d in &diagnostics {
        eprintln!("[{:?}] {}: {}", d.severity, d.code, d.message);
    }

    // Generate
    if let Err(e) = renderer::generate(&model, &output_dir) {
        eprintln!("Generation failed: {}", e);
        process::exit(1);
    }

    eprintln!(
        "Generated Python CLI '{}' with {} resources at {}",
        model.cli_name,
        model.resources.len(),
        output_dir.display()
    );
}
