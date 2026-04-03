#!/bin/bash
# Generate, build, and run the Stripe CLI
# Usage: STRIPE_API_KEY=sk_test_... ./scripts/demo-stripe.sh
set -euo pipefail

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

REPO_ROOT="$(cd "$(dirname "$0")/.."; pwd)"
OUTDIR="/tmp/stripe-cli-demo"
CLI="$OUTDIR/stripe-cli"

echo "=== Building cli-builder ==="
dotnet build "$REPO_ROOT" --verbosity quiet

echo ""
echo "=== Generating Stripe CLI ==="
rm -rf "$OUTDIR"

# Build a tiny project that references Stripe.net to get all DLLs in one directory
SDK_PROJ="/tmp/stripe-sdk-deps"
rm -rf "$SDK_PROJ"
mkdir -p "$SDK_PROJ"
cat > "$SDK_PROJ/stripe-sdk-deps.csproj" << 'SDKEOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Library</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Stripe.net" Version="51.0.0" />
  </ItemGroup>
</Project>
SDKEOF
echo "Resolving Stripe SDK dependencies..."
dotnet publish "$SDK_PROJ" -c Release --verbosity quiet -o "$SDK_PROJ/out"
STRIPE_DLL="$SDK_PROJ/out/Stripe.net.dll"

if [ ! -f "$STRIPE_DLL" ]; then
    echo "ERROR: Could not build Stripe SDK dependency project."
    exit 1
fi
echo "Using Stripe SDK: $STRIPE_DLL"

# Generate via helper console app
HELPER="/tmp/cli-gen-stripe-helper"
rm -rf "$HELPER"
mkdir -p "$HELPER"
cat > "$HELPER/Program.cs" << 'CSEOF'
using CliBuilder.Adapter.DotNet;
using CliBuilder.Core.Models;
using CliBuilder.Generator.CSharp;
var sdkDll = args[0];
var outputDir = args[1];
var adapter = new DotNetAdapter();
var result = adapter.Extract(new AdapterOptions(sdkDll));
var generator = new CSharpCliGenerator();
generator.Generate(result.Metadata, new GeneratorOptions(outputDir, "stripe-cli"));
CSEOF
cat > "$HELPER/cli-gen-stripe-helper.csproj" << XMLEOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$REPO_ROOT/src/CliBuilder.Core/CliBuilder.Core.csproj" />
    <ProjectReference Include="$REPO_ROOT/src/CliBuilder.Adapter.DotNet/CliBuilder.Adapter.DotNet.csproj" />
    <ProjectReference Include="$REPO_ROOT/src/CliBuilder.Generator.CSharp/CliBuilder.Generator.CSharp.csproj" />
  </ItemGroup>
</Project>
XMLEOF

dotnet run --project "$HELPER" -- "$STRIPE_DLL" "$OUTDIR"

echo ""
echo "=== Building generated Stripe CLI ==="
dotnet build "$CLI" --verbosity quiet

echo ""
echo "=========================================="
echo "  Stripe CLI generated and built!"
echo "  136 resources from Stripe.net 51.0.0"
echo "=========================================="
echo ""
echo "The CLI is at: $CLI"
echo ""
echo "=== Quick test ==="
echo ""

echo "$ stripe-cli --help"
dotnet run --project "$CLI" --no-build -- --help
echo ""

API_KEY="${STRIPE_API_KEY:-}"
if [ -n "$API_KEY" ]; then
    echo "$ stripe-cli payment-intent list --json --api-key sk_test_..."
    dotnet run --project "$CLI" --no-build -- payment-intent list --json --api-key "$API_KEY"
    echo ""
else
    echo "Set STRIPE_API_KEY=sk_test_... to test real API calls:"
    echo "  STRIPE_API_KEY=sk_test_... ./scripts/demo-stripe.sh"
fi

echo ""
echo "=== Run your own commands ==="
echo "  dotnet run --project $CLI --no-build -- payment-intent list --json --api-key YOUR_KEY"
echo "  dotnet run --project $CLI --no-build -- payment-intent create --json --api-key YOUR_KEY"
echo "  dotnet run --project $CLI --no-build -- charge list --json --api-key YOUR_KEY"
echo "  dotnet run --project $CLI --no-build -- invoice list --json --api-key YOUR_KEY"
