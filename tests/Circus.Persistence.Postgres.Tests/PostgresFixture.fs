module Circus.Persistence.Postgres.Tests.PostgresFixture

open System
open System.Threading.Tasks
open DotNet.Testcontainers.Containers
open Npgsql
open Circus.Persistence.Postgres

/// Fixture that manages a Testcontainers PostgreSQL instance.
type PostgresFixture(connectionString: string) =
    let container =
        let c = PostgreSqlBuilder()
            .WithImage("postgres:17.4")
            .WithDatabase("circus_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build()
        c.StartAsync() |> WaitTask |> ignore
        c

    let dataSource =
        let builder = NpgsqlDataSourceBuilder container.ConnectionString
        builder.Build()

    member this.ConnectionString: string = container.ConnectionString
    member this.DataSource: NpgsqlDataSource = dataSource

    member this.RunMigration(): Task = 
        Migration.migrate dataSource

    member this.Dispose() =
        container.StopAsync() |> WaitTask |> ignore
        dataSource.Dispose()

/// Create a fresh fixture with migration applied.
let createFixture (): PostgresFixture = 
    let fixture = PostgresFixture("")
    fixture.RunMigration() |> WaitTask |> ignore
    fixture
