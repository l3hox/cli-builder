#!/bin/bash
# Generate code coverage report for cli-builder
# Usage: ./scripts/coverage.sh [--open]
set -euo pipefail

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
COVERAGE_DIR="$REPO_ROOT/coverage"

rm -rf "$COVERAGE_DIR"
mkdir -p "$COVERAGE_DIR"

echo "Running tests with coverage collection..."
dotnet test "$REPO_ROOT" \
    --collect:"XPlat Code Coverage" \
    --results-directory "$COVERAGE_DIR/results" \
    --verbosity quiet \
    -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura \
       DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include="[CliBuilder.Core]*,[CliBuilder.Generator.CSharp]*,[CliBuilder.Adapter.DotNet]*" \
       DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude="[*.Tests]*"

# Find all coverage files
COVERAGE_FILES=$(find "$COVERAGE_DIR/results" -name "coverage.cobertura.xml" | tr '\n' ';')

if [ -z "$COVERAGE_FILES" ]; then
    echo "No coverage files found!"
    exit 1
fi

echo ""
echo "Generating report..."
reportgenerator \
    -reports:"$COVERAGE_FILES" \
    -targetdir:"$COVERAGE_DIR/report" \
    -reporttypes:"TextSummary;Html" \
    -assemblyfilters:"+CliBuilder.Core;+CliBuilder.Generator.CSharp;+CliBuilder.Adapter.DotNet"

echo ""
echo "=== Coverage Summary ==="
cat "$COVERAGE_DIR/report/Summary.txt"
echo ""
echo "Full HTML report: $COVERAGE_DIR/report/index.html"

if [ "${1:-}" = "--open" ] && command -v xdg-open &>/dev/null; then
    xdg-open "$COVERAGE_DIR/report/index.html"
fi
