module Circus.Persistence.Postgres.Tests.PostgresFixture

open System
open System.IO
open System.Reflection
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

    /// Create a fresh database that exists only for one migration test.
    /// The connection string is the runtime credential against the new
    /// database; both data sources are owned by the fixture and disposed
    /// with the rest of the container state.
    member _.CreateMigrationDatabase(databaseName: string) : NpgsqlDataSource * NpgsqlDataSource =
        let connStr = container.GetConnectionString()

        let adminBuilder = NpgsqlConnectionStringBuilder(connStr)
        adminBuilder.Database <- databaseName

        let adminDataSource' =
            NpgsqlDataSourceBuilder(adminBuilder.ConnectionString).Build()

        // The newly created database has no users other than the postgres
        // superuser.  Provision circus_app the same way the main fixture does
        // so the runtime data source can authenticate.
        use prov = adminDataSource.CreateConnection()
        prov.OpenAsync() |> waitUnit
        use provCmd = prov.CreateCommand()
        provCmd.CommandText <- $"CREATE DATABASE \"{databaseName}\""
        wait (provCmd.ExecuteNonQueryAsync()) |> ignore

        // Provision the runtime role inside the new database.
        use prov2 = adminDataSource'.CreateConnection()
        prov2.OpenAsync() |> waitUnit
        use prov2Cmd = prov2.CreateCommand()
        prov2Cmd.CommandText <- "ALTER ROLE circus_app WITH PASSWORD 'circus_test_runtime_password'"
        wait (prov2Cmd.ExecuteNonQueryAsync()) |> ignore

        let runtimeBuilder = NpgsqlConnectionStringBuilder(connStr)
        runtimeBuilder.Database <- databaseName
        runtimeBuilder.Username <- "circus_app"
        runtimeBuilder.Password <- "circus_test_runtime_password"

        let runtimeDataSource' =
            NpgsqlDataSourceBuilder(runtimeBuilder.ConnectionString).Build()

        adminDataSource', runtimeDataSource'

    member _.DropMigrationDatabase(databaseName: string) =
        // Terminate any active connections to the database before dropping it.
        use kill = adminDataSource.CreateConnection()
        kill.OpenAsync() |> waitUnit
        use killCmd = kill.CreateCommand()

        killCmd.CommandText <-
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{databaseName}' AND pid <> pg_backend_pid()"

        wait (killCmd.ExecuteNonQueryAsync()) |> ignore

        use drop = adminDataSource.CreateConnection()
        drop.OpenAsync() |> waitUnit
        use dropCmd = drop.CreateCommand()
        dropCmd.CommandText <- $"DROP DATABASE IF EXISTS \"{databaseName}\""
        wait (dropCmd.ExecuteNonQueryAsync()) |> ignore

    /// Load a checked-in SQL fixture from the test assembly's embedded
    /// resources.  The fixture name must match the file name under
    /// `tests/fixtures/migrations/`.
    member _.LoadFixture(name: string) : string =
        let assembly = Assembly.GetExecutingAssembly()

        let resourceName =
            assembly.GetManifestResourceNames()
            |> Array.tryFind (fun candidate -> candidate.EndsWith(name, StringComparison.Ordinal))

        match resourceName with
        | Some resource ->
            use stream = assembly.GetManifestResourceStream(resource)
            use reader = new StreamReader(stream)
            reader.ReadToEnd()
        | None -> failwithf "fixture %s not embedded in the test assembly" name

    member _.ExecuteAsAdminOn(dataSource: NpgsqlDataSource, sql: string) =
        use conn = dataSource.CreateConnection()
        conn.OpenAsync() |> waitUnit
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        wait (cmd.ExecuteNonQueryAsync()) |> ignore

    interface IDisposable with
        member _.Dispose() =
            runtimeDataSource.Dispose()
            adminDataSource.Dispose()
            container.StopAsync() |> waitUnit
