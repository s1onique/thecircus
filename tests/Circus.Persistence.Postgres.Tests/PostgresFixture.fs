module Circus.Persistence.Postgres.Tests.PostgresFixture

open System
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Containers
open Testcontainers.PostgreSql
open Npgsql
open Circus.Application
open Circus.Persistence.Postgres

let waitUnit (value: Task) = value.GetAwaiter().GetResult()
let wait<'value> (value: Task<'value>) = value.GetAwaiter().GetResult()

type PostgresFixture() =
    let container: PostgreSqlContainer =
        (new PostgreSqlBuilder("postgres:17.4"))
            .WithDatabase("circus_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build()

    do container.StartAsync() |> waitUnit

    let adminDataSource =
        let builder = NpgsqlDataSourceBuilder(container.GetConnectionString())
        builder.Build()

    do Migration.migrate adminDataSource |> waitUnit

    // This is a database-role test credential, not a producer credential.  It
    // exists only inside the ephemeral integration database.
    do
        use conn = adminDataSource.CreateConnection()
        conn.OpenAsync() |> waitUnit
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "ALTER ROLE circus_app WITH PASSWORD 'circus_test_runtime_password'"
        wait (cmd.ExecuteNonQueryAsync()) |> ignore

    let runtimeConnectionString =
        let builder = NpgsqlConnectionStringBuilder(container.GetConnectionString())
        builder.Username <- "circus_app"
        builder.Password <- "circus_test_runtime_password"
        builder.ConnectionString

    let runtimeDataSource =
        let builder = NpgsqlDataSourceBuilder(runtimeConnectionString)
        builder.Build()

    let ingestion = IngestEventService.create runtimeDataSource
    let journal = JournalRepository.create runtimeDataSource
    let projections = ProjectionRepository.create runtimeDataSource

    member _.ConnectionString = runtimeConnectionString
    member _.AdminDataSource = adminDataSource
    member _.DataSource = runtimeDataSource
    member _.Ingestion = ingestion
    member _.JournalRepo = journal
    member _.ProjectionRepo = projections

    member _.Reset() =
        use conn = adminDataSource.CreateConnection()
        conn.OpenAsync() |> waitUnit
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "TRUNCATE circus.circus_run_projection, circus.circus_event_journal RESTART IDENTITY"
        wait (cmd.ExecuteNonQueryAsync()) |> ignore

    member _.ExecuteAsAdmin(sql: string) =
        use conn = adminDataSource.CreateConnection()
        conn.OpenAsync() |> waitUnit
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        wait (cmd.ExecuteNonQueryAsync()) |> ignore

    interface IDisposable with
        member _.Dispose() =
            runtimeDataSource.Dispose()
            adminDataSource.Dispose()
            container.StopAsync() |> waitUnit
