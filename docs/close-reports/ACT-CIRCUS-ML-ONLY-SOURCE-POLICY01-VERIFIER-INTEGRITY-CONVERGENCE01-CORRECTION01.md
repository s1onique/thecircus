# Close Report: ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01-CORRECTION01

## Schema

```yaml
schema_version: circus-close-report/v2
```

## Summary

Eliminated dishonest test pattern where unavailable-Bash suites passed via `Expect.isTrue true` in executed test bodies. Replaced with explicit `BashAvailability` discriminated union and genuine Expecto `ptest` pending state with structural and execution proofs.

## Implementation Identity (Subject)

```yaml
implementation_commit_oid: 19ff261
implementation_tree_oid: (verified by diff)

implementation:
  subject: P1-3 CORRECTION01: Honest Bash-availability model with structural proofs
  type: test correction
  area: Circus.Tooling.Tests.SourcePolicy.ProcessRunnerTests
```

## Verification Identity

```yaml
schema_version: circus-close-report/v2

act_id: ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01-CORRECTION01
work_package_id: P1-3
verdict: closed

subject:
  implementation_commit_oid: 19ff261
  description: P1-3 CORRECTION01: Honest Bash-availability model with structural proofs

verification:
  tested_commit_oid: 19ff261
  run_id: p1-3-20260720-01
  commands:
    - dotnet test --filter "Bash availability" -c Release
    - dotnet test --filter "Process runner" -c Release
    - git diff --check 19ff261..HEAD
  results:
    tests_total: 42
    tests_passed: 42
    tests_failed: 0
```

## Required Identities Verified

| Identity | Status |
|----------|--------|
| `BashAvailability` DU type | ✅ Present |
| `resolveBashAvailability` | ✅ Present |
| `makeBashDependentTest` generic constructor | ✅ Present |
| `bashAvailabilityTests` | ✅ Present |
| `real-host Bash probe` via makeBashDependentTest | ✅ Present |
| `bashOk` removed | ✅ Removed |
| `Expect.isTrue true` unavailable branch removed | ✅ Removed |
| `ptest` unavailable branch | ✅ Present |
| `containsDishonestBashSkip` extracted function | ✅ Present |
| `activateRegressionGuard` uses `__SOURCE_FILE__` | ✅ Present |

## Point 1: Real-Host Probe Through makeBashDependentTest

```fsharp
makeBashDependentTest
    bashAvailability
    "real-host Bash probe"
    (fun executable ->
        // This body only runs when Bash IS available.
        Expect.isNotEmpty executable
            "resolved Bash executable must be non-empty")
```

On a Bash-unavailable host this produces `ptest` with Pending state, NOT a failure.

## Point 2: Genuine Structural and Execution Proofs

### Point 2a: Structural Proof - Unavailable Branch is Pending

```fsharp
test "unavailable branch produces Pending test (structural)" {
    let generated =
        makeBashDependentTest
            (BashUnavailable "injected")
            "probe"
            (fun _ -> ())

    // Exact structural proof: ptest creates TestLabel with TestCase in Pending state
    match generated with
    | TestLabel(_, TestCase(_, Pending), Pending) -> ()
    | actual -> failtestf "Expected exact Pending TestCase, got %A" actual
}
```

### Point 2b: Structural Proof - Available Branch is NOT Pending

```fsharp
test "available branch does NOT produce Pending test (structural)" {
    let generated =
        makeBashDependentTest
            (BashAvailable "/probe/bash")
            "probe"
            (fun _ -> ())

    // Structural proof: available branch is NOT Pending
    match generated with
    | TestLabel(_, TestCase(_, Pending), Pending) ->
        failtestf "Available branch should NOT be Pending, got: %A" generated
    | _ -> ()
}
```

### Point 2c: Execution Proof - Unavailable Body Does NOT Execute

```fsharp
test "unavailable branch body does NOT execute (execution proof)" {
    let mutable bodyExecuted = false

    let generated =
        makeBashDependentTest
            (BashUnavailable "injected absence")
            "forced unavailable"
            (fun _ -> bodyExecuted <- true)

    // Exact structural proof: the generated test has Pending state
    match generated with
    | TestLabel(_, TestCase(_, Pending), Pending) -> ()
    | actual -> failtestf "Expected exact Pending TestCase, got %A" actual

    // Body canary: set to true if body ever executes
    if bodyExecuted then
        failtestf "Body executed for unavailable test - pending state was violated!"

    // Run the test in-process
    let _exitCode = Tests.runTestsWithCLIArgs [] [||] generated

    // Body MUST NOT have executed (proven by canary)
    if bodyExecuted then
        failtestf "Body executed for unavailable test - pending state was violated!"
}
```

### Point 2d: Execution Proof - Available Body DOES Execute

```fsharp
test "available branch body DOES execute (execution proof)" {
    let mutable bodyExecuted = false

    let generated =
        makeBashDependentTest
            (BashAvailable "/probe/bash")
            "forced available"
            (fun _ -> bodyExecuted <- true)

    // Run the test in-process
    let exitCode = Tests.runTestsWithCLIArgs [] [||] generated
    // Exit code 0 means: all tests passed
    if exitCode <> 0 then
        failtestf "Expected pass (exit 0), got exit %d" exitCode

    // Body MUST have executed
    if not bodyExecuted then
        failtestf "Body did not execute for available test - test is vacuous!"
}
```

### Point 2e: Execution Proof - Deliberate Failure Not Converted

```fsharp
test "available test with failing body produces exactly one failure" {
    let generated =
        makeBashDependentTest
            (BashAvailable "/probe/bash")
            "deliberate failure"
            (fun _ ->
                // This will fail
                failwith "intentional failure")

    // Run the test in-process
    let exitCode = Tests.runTestsWithCLIArgs [] [||] generated
    // Exit code 1 means: test failed
    if exitCode <> 1 then
        failtestf "Expected one failure (exit 1), got exit %d" exitCode
}
```

## Point 3: Non-Vacuous Regression Guard

### Extracted Scanner Function

```fsharp
/// Extracts the authoritative dishonest-pattern scanner.
/// Uses IgnoreCase ||| Singleline so that . matches newlines,
/// allowing detection of the old multiline bashOk pattern.
let private containsDishonestBashSkip (source: string) : bool =
    Regex.IsMatch(
        source,
        @"test\s*\""skipped.*bash.*unavailable.*Expect\.isTrue\s+true",
        RegexOptions.IgnoreCase ||| RegexOptions.Singleline)
```

### Regression Guard Activator

```fsharp
let private activateRegressionGuard () =
    let sourceFile = __SOURCE_FILE__

    if not (File.Exists sourceFile) then
        failtestf "source file does not exist: %s" sourceFile

    if containsDishonestBashSkip (File.ReadAllText sourceFile) then
        failtest "dishonest Bash-unavailable test detected. Use ptest instead."
```

### Tests Using Authoritative Scanner

```fsharp
test "regression guard: activateRegressionGuard invokes containsDishonestBashSkip on source" {
    // Invoke the actual regression guard directly
    activateRegressionGuard()
}

test "regression guard: containsDishonestBashSkip rejects negative fixture" {
    let negativeFixture = @"
module Dishonest
let bashOk = true
test ""skipped bash unavailable"" {
    if bashOk then
        Expect.isTrue true
}"
    Expect.isTrue
        (containsDishonestBashSkip negativeFixture)
        "Scanner must reject negative fixture"
}

test "regression guard: containsDishonestBashSkip accepts positive fixture" {
    let positiveFixture = @"
module Honest
ptest ""bash unavailable"" {
    ()
}"
    Expect.isFalse
        (containsDishonestBashSkip positiveFixture)
        "Scanner must accept ptest pattern as honest"
}
```

## Point 4: Correct Bounded Probe

```fsharp
let private resolveBashAvailability () : BashAvailability =
    try
        let psi = ProcessStartInfo(
            FileName = "bash",
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )
        use p = Process.Start(psi)
        if isNull p then
            BashUnavailable "Process.Start returned null"
        else
            try
                let exited = p.WaitForExit(5000)
                if not exited then
                    p.Kill(true)
                    p.WaitForExit(1000) |> ignore
                    BashUnavailable "bash --version timed out"
                elif p.ExitCode = 0 then
                    BashAvailable "bash"
                else
                    BashUnavailable (sprintf "bash exited with code %d" p.ExitCode)
            finally
                p.Dispose()
    with ex ->
        BashUnavailable (sprintf "%s: %s" (ex.GetType().Name) ex.Message)
```

Key properties:
- Only accesses `ExitCode` AFTER confirmed exit
- Kill + second WaitForExit for proper cleanup
- No unused `CancellationTokenSource`

## Test Suite Structure

### Bash Availability Suite (9 tests)
1. `makeBashDependentTest bashAvailability "real-host Bash probe"` - real-host probe
2. `test "unavailable branch produces Pending test (structural)"`
3. `test "available branch does NOT produce Pending test (structural)"`
4. `test "unavailable branch body does NOT execute (execution proof)"`
5. `test "available branch body DOES execute (execution proof)"`
6. `test "available test with failing body produces exactly one failure"`
7. `test "regression guard: activateRegressionGuard invokes containsDishonestBashSkip on source"`
8. `test "regression guard: containsDishonestBashSkip rejects negative fixture"`
9. `test "regression guard: containsDishonestBashSkip accepts positive fixture"`

### ProcessRunner Suite (33 tests)
Standard process-runner behavioral tests (P0-1 through P0-3).

## Before/After Comparison

### Before (Dishonest)
```fsharp
let mutable bashOk = false
do
    try
        let p = Process.Start(...)
        if not (isNull p) then p.Dispose()
        bashOk <- true
    with _ -> ()

if not bashOk then
    test "skipped (bash unavailable)" {
        Expect.isTrue true  // FAKE PASS - body executes!
    }
```

### After (Honest)
```fsharp
type BashAvailability =
    | BashAvailable of executable: string
    | BashUnavailable of reason: string

let makeBashDependentTest (availability: BashAvailability) (name: string) (body: string -> unit) : Test =
    match availability with
    | BashAvailable executable -> test name { body executable }
    | BashUnavailable reason -> ptest (...) { () }  // GENUINE PENDING

makeBashDependentTest bashAvailability "real-host Bash probe" (fun executable -> ...)
```

## Report Content Identity

```yaml
report:
  path: docs/close-reports/ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01-CORRECTION01.md
  content_base_commit_oid: 19ff261
  endpoint_binding: external
```

## Verification Commands

```bash
# Bash-availability tests
dotnet test tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj --filter "Bash availability" -c Release

# ProcessRunner tests
dotnet test tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj --filter "Process runner" -c Release

# Full tooling suite
make test-source-policy

# Patch hygiene
git diff --check 19ff261..HEAD

# Working tree
git status --short
```

## P1-3 CLOSED

```yaml
verdict: closed
work_package_id: P1-3
tests: 42 total (9 availability + 33 ProcessRunner)
patch: clean (git diff --check)
tree: clean (git status --short)
model: honest BashAvailability/ptest with exact pattern matching
endpoint: external (path binding, NOT commit ID)
```
