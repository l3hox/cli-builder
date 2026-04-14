use std::path::PathBuf;
use std::process;

use clap::Parser;
use cli_builder_core::model_mapper::{self, MapperOptions};
use cli_builder_core::models::AdapterResultEnvelope;
use cli_builder_gen_csharp::csharp_mapper::CSharpProfile;
use cli_builder_gen_csharp::csharp_model;
use cli_builder_gen_csharp::renderer;

#[derive(Parser)]
#[command(name = "cli-builder-gen-csharp", about = "Generate a System.CommandLine C# CLI from SdkMetadata JSON")]
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

    /// Path to local SDK .csproj (uses ProjectReference instead of PackageReference)
    #[arg(long)]
    sdk_project_path: Option<String>,
}

fn main() {
    let args = Args::parse();

    let json = std::fs::read_to_string(&args.input).unwrap_or_else(|e| {
        eprintln!("Failed to read {}: {}", args.input.display(), e);
        process::exit(1);
    });
    let envelope: AdapterResultEnvelope = serde_json::from_str(&json).unwrap_or_else(|e| {
        eprintln!("Failed to parse metadata JSON: {}", e);
        process::exit(1);
    });

    let profile = CSharpProfile;
    let options = MapperOptions {
        cli_name: args.cli_name,
    };
    let (model, diagnostics) = model_mapper::build(&envelope.metadata, &options, &profile);

    for d in &diagnostics {
        eprintln!("[{:?}] {}: {}", d.severity, d.code, d.message);
    }

    let mut diags = Vec::new();
    let mut csharp_model = csharp_model::build_csharp_model(&model, &mut diags);

    // Override SDK project path if provided
    if let Some(ref path) = args.sdk_project_path {
        csharp_model.sdk_project_path = Some(path.clone());
    }

    for d in &diags {
        eprintln!("[{:?}] {}: {}", d.severity, d.code, d.message);
    }

    if let Err(e) = renderer::generate(&csharp_model, &args.output) {
        eprintln!("Generation failed: {}", e);
        process::exit(1);
    }

    eprintln!(
        "Generated C# CLI '{}' with {} resources at {}",
        csharp_model.cli_name,
        csharp_model.resources.len(),
        args.output.display()
    );
}
