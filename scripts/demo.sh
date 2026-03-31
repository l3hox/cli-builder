#!/bin/bash
# Generate, build, and interactively use the TestSdk CLI
# Usage: ./scripts/demo.sh
set -euo pipefail

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

REPO_ROOT="$(cd "$(dirname "$0")/.."; pwd)"
OUTDIR="/tmp/testsdk-cli-demo"
CLI="$OUTDIR/testsdk-cli"

echo "=== Building cli-builder ==="
dotnet build "$REPO_ROOT" --verbosity quiet

echo ""
echo "=== Generating TestSdk CLI ==="
rm -rf "$OUTDIR"

# Extract metadata + generate CLI (using the public API directly via a helper)
dotnet run --project "$REPO_ROOT/scripts/GenerateHelper" -- \
    "$REPO_ROOT/tests/CliBuilder.TestSdk/bin/Debug/net8.0/CliBuilder.TestSdk.dll" \
    "$OUTDIR" \
    "$REPO_ROOT/tests/CliBuilder.TestSdk/CliBuilder.TestSdk.csproj" 2>/dev/null \
|| {
    # Fallback: generate via inline C# script if helper doesn't exist
    echo "Helper project not found, generating via test runner..."
    # Create a minimal console app that generates the CLI
    HELPER="/tmp/cli-gen-helper"
    rm -rf "$HELPER"
    mkdir -p "$HELPER"
    cat > "$HELPER/Program.cs" << 'CSEOF'
using CliBuilder.Adapter.DotNet;
using CliBuilder.Core.Models;
using CliBuilder.Generator.CSharp;

var sdkDll = args[0];
var outputDir = args[1];
var csprojPath = args[2];

var adapter = new DotNetAdapter();
var result = adapter.Extract(new AdapterOptions(sdkDll));
var generator = new CSharpCliGenerator();
generator.Generate(result.Metadata,
    new GeneratorOptions(outputDir, "testsdk-cli", SdkProjectPath: csprojPath));
CSEOF
    cat > "$HELPER/cli-gen-helper.csproj" << 'XMLEOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="REPO_ROOT/src/CliBuilder.Core/CliBuilder.Core.csproj" />
    <ProjectReference Include="REPO_ROOT/src/CliBuilder.Adapter.DotNet/CliBuilder.Adapter.DotNet.csproj" />
    <ProjectReference Include="REPO_ROOT/src/CliBuilder.Generator.CSharp/CliBuilder.Generator.CSharp.csproj" />
  </ItemGroup>
</Project>
XMLEOF
    sed -i "s|REPO_ROOT|$REPO_ROOT|g" "$HELPER/cli-gen-helper.csproj"
    dotnet run --project "$HELPER" -- \
        "$REPO_ROOT/tests/CliBuilder.TestSdk/bin/Debug/net8.0/CliBuilder.TestSdk.dll" \
        "$OUTDIR" \
        "$REPO_ROOT/tests/CliBuilder.TestSdk/CliBuilder.TestSdk.csproj"
}

echo ""
echo "=== Building generated CLI ==="
dotnet build "$CLI" --verbosity quiet

echo ""
echo "=== Demo ==="
echo ""

run() {
    echo "$ testsdk-cli $*"
    dotnet run --project "$CLI" --no-build -- "$@"
    echo ""
}

run --help
run customer get --id cust_42 --json --api-key demo
run customer create --email hello@world.com --preferred-contact true --json --api-key demo
run customer list --json --api-key demo
run product list --json --api-key demo

echo "=== Interactive ==="
echo ""
echo "The generated CLI is at: $CLI"
echo ""
echo "Run your own commands:"
echo "  dotnet run --project $CLI --no-build -- customer get --id YOUR_ID --json --api-key demo"
echo "  dotnet run --project $CLI --no-build -- customer stream --json --api-key demo"
echo "  dotnet run --project $CLI --no-build -- order get --id ord_1 --json --api-key demo"
echo "  dotnet run --project $CLI --no-build -- customer get-metadata --id m1 --json --api-key demo"
