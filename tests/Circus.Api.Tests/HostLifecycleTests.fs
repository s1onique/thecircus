module Circus.Api.Tests.HostLifecycleTests

open System
open System.Threading
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Containers
open Expecto
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Testcontainers.PostgreSql
open Npgsql
open Circus.Api.Program
open Circus.Api.IngestionHandlers
open Circus.Persistence.Postgres

let waitUnit (value: Task) = value.GetAwaiter().GetResult()
let wait (value: Task<'value>) = value.GetAwaiter().GetResult()

let private runWithEnv (key: string) (value: string option) (f: unit -> 'a) : 'a =
    let previous =
        match Environment.GetEnvironmentVariable key with
        | null -> None
        | v -> Some v

    try
        match value with
        | Some v -> Environment.SetEnvironmentVariable(key, v)
        | None -> Environment.SetEnvironmentVariable(key, null)

        f ()
    finally
        match previous with
        | Some v -> Environment.SetEnvironmentVariable(key, v)
        | None -> Environment.SetEnvironmentVariable(key, null)

let tests =
    testList
        "Host lifecycle and data source ownership"
        [ test "missing CIRCUS_DATABASE_URL fails startup" {
              runWithEnv "CIRCUS_DATABASE_URL" None (fun () ->
                  Expect.throws
                      (fun () -> postgresConnectionString () |> ignore)
                      "Missing connection string fails validation")
          }

          test "empty CIRCUS_DATABASE_URL fails startup" {
              runWithEnv "CIRCUS_DATABASE_URL" (Some "") (fun () ->
                  Expect.throws
                      (fun () -> postgresConnectionString () |> ignore)
                      "Empty connection string fails validation")
          }

          test "whitespace CIRCUS_DATABASE_URL fails startup" {
              runWithEnv "CIRCUS_DATABASE_URL" (Some "   ") (fun () ->
                  Expect.throws
                      (fun () -> postgresConnectionString () |> ignore)
                      "Whitespace connection string fails validation")
          }

          test "malformed CIRCUS_DATABASE_URL fails startup" {
              runWithEnv "CIRCUS_DATABASE_URL" (Some "not a postgres url") (fun () ->
                  Expect.throws
                      (fun () -> postgresConnectionString () |> ignore)
                      "Malformed connection string fails validation")
          }

          test "valid CIRCUS_DATABASE_URL permits host construction and singleton lifetime" {
              let container: PostgreSqlContainer =
                  (new PostgreSqlBuilder("postgres:17.4"))
                      .WithDatabase("circus_lifecycle_test")
                      .WithUsername("postgres")
                      .WithPassword("postgres")
                      .Build()

              container.StartAsync() |> waitUnit

              let adminDataSource =
                  NpgsqlDataSourceBuilder(container.GetConnectionString()).Build()

              Migration.migrate adminDataSource |> waitUnit

              try
                  let connectionString = container.GetConnectionString()

                  runWithEnv "CIRCUS_DATABASE_URL" (Some connectionString) (fun () ->
                      let services = new ServiceCollection()
                      services.AddGiraffe() |> ignore

                      services.AddSingleton<NpgsqlDataSource>(fun _ ->
                          postgresConnectionString ()
                          |> PostgresConfiguration.defaultConfiguration
                          |> PostgresConfiguration.createDataSource)
                      |> ignore

                      let sp = services.BuildServiceProvider()

                      let first = sp.GetRequiredService<NpgsqlDataSource>()
                      let second = sp.GetRequiredService<NpgsqlDataSource>()

                      Expect.equal (first :> obj) (second :> obj) "Singleton identity preserved"

                      use conn = first.CreateConnection()
                      conn.OpenAsync() |> waitUnit

                      use cmd = conn.CreateCommand()
                      cmd.CommandText <- "SELECT 1"
                      let value = cmd.ExecuteScalar() :?> int
                      Expect.equal value 1 "Resolved data source accepts commands")
              finally
                  adminDataSource.Dispose()
                  container.StopAsync() |> waitUnit
          }

          test "disposing the host service provider disposes the data source" {
              let container: PostgreSqlContainer =
                  (new PostgreSqlBuilder("postgres:17.4"))
                      .WithDatabase("circus_lifecycle_dispose_test")
                      .WithUsername("postgres")
                      .WithPassword("postgres")
                      .Build()

              container.StartAsync() |> waitUnit

              try
                  let connectionString = container.GetConnectionString()

                  runWithEnv "CIRCUS_DATABASE_URL" (Some connectionString) (fun () ->
                      let services = new ServiceCollection()

                      services.AddSingleton<NpgsqlDataSource>(fun _ ->
                          postgresConnectionString ()
                          |> PostgresConfiguration.defaultConfiguration
                          |> PostgresConfiguration.createDataSource)
                      |> ignore

                      let sp = services.BuildServiceProvider()
                      let dataSource = sp.GetRequiredService<NpgsqlDataSource>()

                      use _probe = dataSource.CreateConnection()
                      _probe.OpenAsync() |> waitUnit

                      sp.Dispose()

                      Expect.throws
                          (fun () ->
                              use _after = dataSource.CreateConnection()
                              _after.OpenAsync() |> waitUnit)
                          "Data source is disposed when the provider is disposed")
              finally
                  container.StopAsync() |> waitUnit
          } ]
