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

[<EntryPoint>]
let main (args: string[]) =
    let fixture = new PostgresFixture()

    try
        // Every test in this executable shares the same PostgresFixture.
        // The fixture owns one PostgreSQL container and one NpgsqlDataSource,
        // and tests interact with the same database, the same roles, and
        // the same trigger / privilege state.  Expecto's default execution
        // runs tests in parallel; that would interleave truncate / grant
        // / trigger operations across tests and produce flakes.  The outer
        // testSequenced serialises the top-level groups; each inner group
        // is also wrapped in testSequenced so that every test in the suite
        // runs one-at-a-time.
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
                      testSequenced (ProjectionInvariantTests.tests) ]
            )

        Tests.runTestsWithCLIArgs [||] args allTests
    finally
        (fixture :> IDisposable).Dispose()
