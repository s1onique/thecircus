# Test Authority Documentation

## Test Platform

- **Framework**: Expecto 11.1.0
- **Target**: .NET 10.0

## Canonical Test Command

```bash
dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-build -- --filter "TestName"
```

## Alternative Runner

```bash
./tests/RunTests.sh [--filter "TestName"]
```

## Note on dotnet test

The standard `dotnet test` command is unavailable due to a testhost preview version mismatch (18.3.0-release-26180-118 not available in .NET 10 SDK). This is a known issue with Expecto 11.1.0's VSTest dependency.

## Test Categories

- `RepairEpisodeVerification` - Verification evidence loading tests (20 tests)
- `CliSubprocess` - CLI subprocess tests (9 tests)
- `CanonicalPreservation` - Canonical file preservation tests (5 tests)
