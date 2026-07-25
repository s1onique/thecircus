module Circus.Persistence.Postgres.Tests.Runner.Smoke.Program

// =============================================================================
// Runner.Smoke entry point
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION01
//
// ``[<EntryPoint>]`` for the hermetic test executable.  This entry
// point never instantiates a PostgresFixture, never references
// Testcontainers, and never opens a PostgreSQL connection.  It only
// runs the four hermetic tests that prove the runner seam preserves
// every integer the underlying runner returns.
//
// The OS exit code is exactly whatever the Expecto runner returns,
// because the entry point delegates to ``PostgresTestRunner.runWith``.
// Zero on success; one if a test failed; two if a test errored; 37 in
// the artificial case where the seam translates a non-zero into a
// different value (which must not happen).
// =============================================================================

open Expecto
open Circus.Persistence.Postgres.Tests.Runner
open Circus.Persistence.Postgres.Tests.Runner.Smoke.PostgresTestRunnerExitCodeTests

[<EntryPoint>]
let main (args: string[]) =
    let allTests =
        testSequenced (
            testList
                "Circus.Persistence.Postgres.Tests.Runner.Smoke"
                [ testSequenced PostgresTestRunnerExitCodeTests.tests ]
        )
    PostgresTestRunner.runWith Tests.runTestsWithCLIArgs args allTests
