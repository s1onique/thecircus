module Circus.Persistence.Postgres.Tests.Program

open System
open Expecto
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.MigrationTests
open Circus.Persistence.Postgres.Tests.JournalRepositoryTests
open Circus.Persistence.Postgres.Tests.ConcurrencyTests
open Circus.Persistence.Postgres.Tests.ProjectionIntegrationTests
open Circus.Persistence.Postgres.Tests.AppendFailedRollbackTests
open Circus.Persistence.Postgres.Tests.RetryCompositionTests
open Circus.Persistence.Postgres.Tests.SemanticReplayTests
open Circus.Persistence.Postgres.Tests.ProjectionInvariantTests
open Circus.Persistence.Postgres.Tests.UnlockFailureTests
open Circus.Persistence.Postgres.Tests.PostgresTestRunner
open Circus.Persistence.Postgres.Tests.PostgresTestRunnerExitCodeTests

[<EntryPoint>]
let main (args: string[]) =
    // Every test in this executable shares the same PostgresFixture.
    // The fixture owns one PostgreSQL container and one NpgsqlDataSource,
    // and tests interact with the same database, the same roles, and
    // the same trigger / privilege state.  Expecto's default execution
    // runs tests in parallel; that would interleave truncate / grant
    // / trigger operations across tests and produce flakes.  The outer
    // testSequenced serialises the top-level groups; each inner group
    // is also wrapped in testSequenced so that every test in the suite
    // runs one-at-a-time.
    //
    // `use` disposes the fixture as soon as the entry point's scope
    // ends, which is after `runWith` returns.  The seam itself does
    // not touch the fixture; disposal remains an entry-point concern
    // so that the seam can be exercised hermetically.
    use fixture = new PostgresFixture()

    let allTests =
        testSequenced (
            testList
                "Circus.Persistence.Postgres.Tests"
                [ testSequenced (MigrationTests.tests fixture)
                  testSequenced (UnlockFailureTests.tests)
                  testSequenced (JournalRepositoryTests.tests fixture)
                  testSequenced (ConcurrencyTests.tests fixture)
                  testSequenced (ProjectionIntegrationTests.tests fixture)
                  testSequenced (AppendFailedRollbackTests.tests fixture)
                  testSequenced (RetryCompositionTests.tests fixture)
                  testSequenced (SemanticReplayTests.tests fixture)
                  testSequenced (ProjectionInvariantTests.tests)
                  testSequenced PostgresTestRunnerExitCodeTests.tests ]
        )

    PostgresTestRunner.runWith Tests.runTestsWithCLIArgs args allTests
