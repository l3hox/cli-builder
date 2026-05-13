//! cli-builder — generate agent-ready CLIs from SDK packages.

mod adapter;
mod diagnostics;
mod generate;
mod inspect;

use clap::{Parser, Subcommand, ValueEnum};

use adapter::AdapterKind;
use generate::{GenerateOptions, GeneratorTarget};
use inspect::InspectOptions;

#[derive(Parser)]
#[command(
    name = "cli-builder",
    about = "Generate agent-ready CLIs from SDK packages — any language in, any language out",
    version
)]
struct Cli {
    #[command(subcommand)]
    command: Commands,
}

#[derive(Subcommand)]
enum Commands {
    /// Generate a CLI project from an SDK package
    Generate {
        /// Adapter to use for metadata extraction
        #[arg(long, value_enum)]
        adapter: AdapterArg,

        /// .NET SDK assembly path (dotnet adapter)
        #[arg(long, required_if_eq("adapter", "dotnet"))]
        assembly: Option<String>,

        /// Python package name (python adapter)
        #[arg(long, required_if_eq("adapter", "python"))]
        package: Option<String>,

        /// Python module within package (optional)
        #[arg(long)]
        module: Option<String>,

        /// Force single-client discovery on this class (python adapter, ADR-023)
        #[arg(long)]
        entry_class: Option<String>,

        /// Generator target language
        #[arg(long, value_enum)]
        generator: Option<GeneratorArg>,

        /// Output directory
        #[arg(long)]
        output: String,

        /// CLI name (derived from SDK name if omitted)
        #[arg(long)]
        cli_name: Option<String>,

        /// Replace existing output directory
        #[arg(long)]
        overwrite: bool,

        /// Local SDK .csproj path (C# generator: ProjectReference instead of PackageReference)
        #[arg(long)]
        sdk_project_path: Option<String>,
    },

    /// Inspect SDK metadata without generating
    Inspect {
        /// Adapter to use for metadata extraction
        #[arg(long, value_enum)]
        adapter: AdapterArg,

        /// .NET SDK assembly path (dotnet adapter)
        #[arg(long, required_if_eq("adapter", "dotnet"))]
        assembly: Option<String>,

        /// Python package name (python adapter)
        #[arg(long, required_if_eq("adapter", "python"))]
        package: Option<String>,

        /// Python module within package (optional)
        #[arg(long)]
        module: Option<String>,

        /// Force single-client discovery on this class (python adapter, ADR-023)
        #[arg(long)]
        entry_class: Option<String>,

        /// Output raw JSON envelope instead of human-readable summary
        #[arg(long)]
        json: bool,
    },
}

#[derive(Clone, ValueEnum)]
enum AdapterArg {
    Dotnet,
    Python,
}

#[derive(Clone, ValueEnum)]
enum GeneratorArg {
    Csharp,
    Python,
}

fn main() {
    let cli = Cli::parse();

    let exit_code = match cli.command {
        Commands::Generate {
            adapter,
            assembly,
            package,
            module,
            entry_class,
            generator,
            output,
            cli_name,
            overwrite,
            sdk_project_path,
        } => {
            let adapter_kind = build_adapter_kind(&adapter, assembly, package, module, entry_class);
            let generator_target = match generator {
                Some(GeneratorArg::Csharp) => GeneratorTarget::CSharp,
                Some(GeneratorArg::Python) => GeneratorTarget::Python,
                None => match adapter {
                    AdapterArg::Dotnet => GeneratorTarget::CSharp,
                    AdapterArg::Python => GeneratorTarget::Python,
                },
            };

            generate::run(GenerateOptions {
                adapter: adapter_kind,
                generator: generator_target,
                output_dir: output,
                cli_name,
                overwrite,
                sdk_project_path,
            })
        }

        Commands::Inspect {
            adapter,
            assembly,
            package,
            module,
            entry_class,
            json,
        } => {
            let adapter_kind = build_adapter_kind(&adapter, assembly, package, module, entry_class);
            inspect::run(InspectOptions {
                adapter: adapter_kind,
                json,
            })
        }
    };

    std::process::exit(exit_code);
}

fn build_adapter_kind(
    adapter: &AdapterArg,
    assembly: Option<String>,
    package: Option<String>,
    module: Option<String>,
    entry_class: Option<String>,
) -> AdapterKind {
    match adapter {
        AdapterArg::Dotnet => AdapterKind::DotNet {
            assembly: assembly.expect("--assembly required for dotnet adapter"),
        },
        AdapterArg::Python => AdapterKind::Python {
            package: package.expect("--package required for python adapter"),
            module,
            entry_class,
        },
    }
}
