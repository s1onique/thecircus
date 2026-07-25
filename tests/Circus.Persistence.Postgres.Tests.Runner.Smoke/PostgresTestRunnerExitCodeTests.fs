module Circus.Persistence.Postgres.Tests.Runner.Smoke.PostgresTestRunnerExitCodeTests

// =============================================================================
// PostgresTestRunnerExitCodeTests – hermetic tests
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION01
//
// Hermetic tests that prove ``PostgresTestRunner.runWith`` preserves
// whatever integer the underlying runner returns.  These tests do not
// touch PostgreSQL; they substitute a runner that returns a fixed
// integer and assert the seam returns that integer unchanged.
//
// The contract that this module encodes:
//
//   * zero stays zero;
//   * every distinct non-zero integer survives the seam untouched;
//   * an arbitrary representative non-zero value (37) reaches the
//     caller exactly;
//   * the runner is invoked with the argv that the caller passed and
//     with no default arguments (per the production implementation).
//
// The test executable that hosts this module does NOT include
// ``PostgresFixture`` or ``Testcontainers``.  It depends only on
// Expecto and the small ``Circus.Persistence.Postgres.Tests.Runner``
// support library.  Therefore the test executable can run on hosts
// without Docker or PostgreSQL installed.
// =============================================================================

open Expecto
open Circus.Persistence.Postgres.Tests.Runner.PostgresTestRunner

let private fakeRunner (result: int) : CLIArguments seq -> string array -> Test -> int =
    fun (defaults: CLIArguments seq) (argv: string array) (_: Test) ->
        Expect.isEmpty defaults "seam calls runner with no default CLIArguments"
        Expect.equal argv [| "alpha"; "beta" |] "seam passes argv through to runner"
        result

let tests =
    testList
        "Postgres test runner exit code"
        [ test "passing runner returns 0" {
              let argv = [| "alpha"; "beta" |]
              let exit = runWith (fakeRunner 0) argv (testList "unused" [])
              Expect.equal exit 0 "passing runner exits 0"
          }

          test "failed runner returns 1" {
              let argv = [| "alpha"; "beta" |]
              let exit = runWith (fakeRunner 1) argv (testList "unused" [])
              Expect.equal exit 1 "failed runner exits 1"
          }

          test "errored runner returns 2" {
              let argv = [| "alpha"; "beta" |]
              let exit = runWith (fakeRunner 2) argv (testList "unused" [])
              Expect.equal exit 2 "errored runner exits 2"
          }

          test "arbitrary non-zero runner returns its exact value (37)" {
              let argv = [| "alpha"; "beta" |]
              let exit = runWith (fakeRunner 37) argv (testList "unused" [])
              Expect.equal exit 37 "arbitrary non-zero value survives the seam"
          } ]
