module Circus.Persistence.Postgres.Tests.Program

open System
open Expecto
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.MigrationTests
open Circus.Persistence.Postgres.Tests.JournalRepositoryTests
open Circus.Persistence.Postgres.Tests.ConcurrencyTests
open Circus.Persistence.Postgres.Tests.ProjectionIntegrationTests

[<EntryPoint>]
let main (args: string[]) =
    let fixture = new PostgresFixture()

    try
        let allTests =
            testSequenced (
                testList
                    "Circus.Persistence.Postgres.Tests"
                    [ MigrationTests.tests fixture
                      JournalRepositoryTests.tests fixture
                      ConcurrencyTests.tests fixture
                      ProjectionIntegrationTests.tests fixture ]
            )

        Tests.runTestsWithCLIArgs [||] args allTests
    finally
        (fixture :> IDisposable).Dispose()
