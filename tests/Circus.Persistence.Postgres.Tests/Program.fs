module Circus.Persistence.Postgres.Tests.Program

open Expecto
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.MigrationTests
open Circus.Persistence.Postgres.Tests.JournalRepositoryTests
open Circus.Persistence.Postgres.Tests.ConcurrencyTests
open Circus.Persistence.Postgres.Tests.ProjectionIntegrationTests

[<EntryPoint>]
let main (args: string[]) =
    let fixture = createFixture ()

    try
        let allTests =
            testList
                "Circus.Persistence.Postgres.Tests"
                [ migrationTests fixture
                  journalRepositoryTests fixture
                  concurrencyTests fixture
                  projectionIntegrationTests fixture ]

        let rc = Tests.runTestsWithCLIArgs [||] args allTests
        (fixture :> System.IDisposable).Dispose()
        rc
    with
    | ex ->
        (fixture :> System.IDisposable).Dispose()
        reraise ()
