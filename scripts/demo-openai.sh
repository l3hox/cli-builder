#!/bin/bash
# Generate, build, and run the OpenAI CLI
# Usage: OPENAI_API_KEY=sk-... ./scripts/demo-openai.sh
#    or: ./scripts/demo-openai.sh --api-key sk-...
set -euo pipefail

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

REPO_ROOT="$(cd "$(dirname "$0")/.."; pwd)"
OUTDIR="/tmp/openai-cli-demo"
CLI="$OUTDIR/openai-cli"

echo "=== Building cli-builder ==="
dotnet build "$REPO_ROOT" --verbosity quiet

echo ""
echo "=== Generating OpenAI CLI ==="
rm -rf "$OUTDIR"

# Generate via a helper console app
HELPER="/tmp/cli-gen-openai-helper"
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
generator.Generate(result.Metadata, new GeneratorOptions(outputDir, "openai-cli"));
CSEOF
cat > "$HELPER/cli-gen-openai-helper.csproj" << XMLEOF
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

# Find the OpenAI SDK DLL (from integration test NuGet restore)
OPENAI_DLL=$(find "$HOME/.nuget/packages/openai/2.9.1" -name "OpenAI.dll" -path "*/net8.0/*" 2>/dev/null | head -1)
if [ -z "$OPENAI_DLL" ]; then
    echo "OpenAI SDK DLL not found. Running dotnet restore to fetch it..."
    dotnet restore "$REPO_ROOT/tests/CliBuilder.Integration.Tests" --verbosity quiet
    OPENAI_DLL=$(find "$HOME/.nuget/packages/openai/2.9.1" -name "OpenAI.dll" -path "*/net8.0/*" 2>/dev/null | head -1)
fi

if [ -z "$OPENAI_DLL" ]; then
    echo "ERROR: Could not find OpenAI.dll. Ensure the OpenAI NuGet package is restored."
    exit 1
fi
echo "Using OpenAI SDK: $OPENAI_DLL"

dotnet run --project "$HELPER" -- "$OPENAI_DLL" "$OUTDIR"

echo ""
echo "=== Building generated OpenAI CLI ==="
dotnet build "$CLI" --verbosity quiet

echo ""
echo "=========================================="
echo "  OpenAI CLI generated and built!"
echo "=========================================="
echo ""
echo "The CLI is at: $CLI"
echo ""
echo "=== What works ==="
echo ""
echo "60/169 operations make real SDK calls. Key working commands:"
echo ""
echo "  FREE TIER (works with any API key):"
echo "    openai-cli open-ai-model get-models --json --api-key YOUR_KEY"
echo "    openai-cli open-ai-model get-model --id gpt-4o --json --api-key YOUR_KEY"
echo ""
echo "  PAID TIER (requires credits):"
echo "    openai-cli fine-tuning get-jobs --json --api-key YOUR_KEY"
echo "    openai-cli open-ai-file get-files --json --api-key YOUR_KEY"
echo "    openai-cli batch get-batch --id batch_xxx --json --api-key YOUR_KEY"
echo "    openai-cli assistant get-assistant --id asst_xxx --json --api-key YOUR_KEY"
echo ""
echo "  NOTE: chat, embedding, image, audio, moderation commands fall back to echo"
echo "  (their SDK clients need multi-arg constructors — step 8 scope)."
echo ""
echo "=== Quick test ==="
echo ""

API_KEY="${OPENAI_API_KEY:-${1:-}}"
if [ -n "$API_KEY" ]; then
    echo "$ openai-cli --help"
    dotnet run --project "$CLI" --no-build -- --help
    echo ""

    echo "$ openai-cli open-ai-model get-models --json --api-key sk-..."
    dotnet run --project "$CLI" --no-build -- open-ai-model get-models --json --api-key "$API_KEY"
    echo ""

    echo "$ openai-cli open-ai-model get-model --id gpt-4o --json --api-key sk-..."
    dotnet run --project "$CLI" --no-build -- open-ai-model get-model --id gpt-4o --json --api-key "$API_KEY"
    echo ""
else
    echo "$ openai-cli --help"
    dotnet run --project "$CLI" --no-build -- --help
    echo ""
    echo "Set OPENAI_API_KEY or pass --api-key to test real API calls:"
    echo "  OPENAI_API_KEY=sk-... ./scripts/demo-openai.sh"
    echo "  ./scripts/demo-openai.sh --api-key sk-..."
fi

echo ""
echo "=== Run your own commands ==="
echo "  dotnet run --project $CLI --no-build -- open-ai-model get-models --json --api-key YOUR_KEY"
echo "  dotnet run --project $CLI --no-build -- fine-tuning get-jobs --json --api-key YOUR_KEY"
echo "  dotnet run --project $CLI --no-build -- open-ai-file get-files --json --api-key YOUR_KEY"
echo "  dotnet run --project $CLI --no-build -- assistant create-assistant --json --api-key YOUR_KEY"
