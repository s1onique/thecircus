#!/bin/sh
# =============================================================================
# Test runner script for circus-tooling tests
#
# ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION05-RUNNER-INTEGRITY01
#
# Test Authority: Expecto direct executable
# Canonical command: dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-build -- --filter "TestName"
#
# Note: dotnet test is unavailable due to testhost preview version mismatch.
# =============================================================================

set -eu

REPO_ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$REPO_ROOT"

exec dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release \
  --no-build \
  -- "$@"
