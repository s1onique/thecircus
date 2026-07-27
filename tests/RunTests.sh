#!/bin/sh
# =============================================================================
# Test runner script for circus-tooling tests
#
# ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION04-RUNNER-AUTHORITY01
#
# Test Authority: Expecto direct executable
# Canonical command: dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-build -- --filter "TestName"
#
# Note: dotnet test is unavailable due to testhost preview version mismatch.
# =============================================================================

set -e

# Get the directory where this script is located
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

cd "$PROJECT_DIR"

# Default filter
FILTER="${1:-}"

if [ -z "$FILTER" ]; then
    echo "Running all tests..."
    dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-build
else
    echo "Running tests with filter: $FILTER"
    dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-build -- --filter "$FILTER"
fi
