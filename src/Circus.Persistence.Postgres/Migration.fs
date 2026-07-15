namespace Circus.Persistence.Postgres

open System
open System.IO
open System.Reflection
open System.Threading.Tasks
open Npgsql

/// Runs the checked-in SQL migrations using an owner/migration connection.
/// The runtime application role is not expected to have DDL privileges.
module Migration =
    let private migrationNames =
        [ "000001_event_journal.sql"; "000002_namespace_alignment.sql" ]

    let private readMigration (name: string) =
        let assembly = Assembly.GetExecutingAssembly()

        let resourceName =
            assembly.GetManifestResourceNames()
            |> Array.tryFind (fun candidate -> candidate.EndsWith(name, StringComparison.Ordinal))

        match resourceName with
        | Some resource ->
            use stream = assembly.GetManifestResourceStream(resource)
            use reader = new StreamReader(stream)
            reader.ReadToEnd()
        | None ->
            let candidates =
                [ Path.Combine(AppContext.BaseDirectory, "db", "migrations", name)
                  Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "db", "migrations", name)
                  Path.Combine(Directory.GetCurrentDirectory(), "db", "migrations", name) ]

            match candidates |> List.tryFind File.Exists with
            | Some path -> File.ReadAllText path
            | None -> failwith "Circus database migration resource is missing"

    let migrate (dataSource: NpgsqlDataSource) : Task<unit> =
        task {
            use! conn = dataSource.OpenConnectionAsync()

            for name in migrationNames do
                use cmd = conn.CreateCommand()
                cmd.CommandText <- readMigration name
                do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
        }
