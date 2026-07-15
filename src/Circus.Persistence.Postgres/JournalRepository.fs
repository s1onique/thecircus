namespace Circus.Persistence.Postgres

open System
open System.Threading.Tasks
open Npgsql
open Circus.Application
open Circus.Domain

type JournalRepository =
    { LookupByIdentity: string -> string -> Task<JournalEntry option>
      LookupByStreamPosition: string -> Guid -> int64 -> Task<JournalEntry option>
      LookupByPosition: int64 -> Task<JournalEntry option>
      CheckEnvelopeEqual: string -> string -> string -> Task<bool>
      LookupByRunId: RunId -> Task<JournalEntry list>
      LookupAll: unit -> Task<JournalEntry list>
      Count: unit -> Task<int64> }

module JournalRepository =
    let create (dataSource: NpgsqlDataSource) : JournalRepository =
        { LookupByIdentity =
            fun source eventId ->
                task {
                    use conn = dataSource.CreateConnection()
                    do! conn.OpenAsync() |> Async.AwaitTask
                    use tx = conn.BeginTransaction()
                    return! JournalSqlExec.lookupByIdentity conn tx source eventId
                }
          LookupByStreamPosition =
            fun instanceId epochId sequence ->
                task {
                    use conn = dataSource.CreateConnection()
                    do! conn.OpenAsync() |> Async.AwaitTask
                    use tx = conn.BeginTransaction()
                    return! JournalSqlExec.lookupByStreamPosition conn tx instanceId epochId sequence
                }
          LookupByPosition =
            fun position ->
                task {
                    use conn = dataSource.CreateConnection()
                    do! conn.OpenAsync() |> Async.AwaitTask
                    use tx = conn.BeginTransaction()
                    return! JournalSqlExec.lookupByPosition conn tx position
                }
          CheckEnvelopeEqual =
            fun source eventId envelope ->
                task {
                    use conn = dataSource.CreateConnection()
                    do! conn.OpenAsync() |> Async.AwaitTask
                    use tx = conn.BeginTransaction()
                    return! JournalSqlExec.checkEnvelopeEqual conn tx source eventId envelope
                }
          LookupByRunId = fun runId -> JournalSqlExec.lookupByRunId dataSource runId
          LookupAll = fun () -> JournalSqlExec.lookupAllOrdered dataSource
          Count = fun () -> JournalSqlExec.count dataSource }
