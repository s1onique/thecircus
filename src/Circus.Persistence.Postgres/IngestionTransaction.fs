namespace Circus.Persistence.Postgres

open System
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open Circus.Application
open Circus.Application.JournalDecision
open Circus.Contracts
open Circus.Domain

type IngestionTxResult =
    | AppendSucceeded of JournalAppendOutcome * RunProjection option
    | AppendFailed of PersistenceFailure

type IngestionTransaction =
    { Execute: NpgsqlConnection -> NpgsqlTransaction -> JournalCandidate -> ValidatedEvent -> Task<IngestionTxResult> }

module IngestionTransaction =

    let maxTotalAttempts = 3

    let loadProjection (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (runId: RunId) : Task<RunProjection option> =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- ProjectionSql.selectByRunId
            cmd.Parameters.AddWithValue("runId", NpgsqlDbType.Uuid, RunId.value runId) |> ignore
            use! reader = cmd.ExecuteReaderAsync()
            if reader.Read() then
                return Some(ProjectionRepository.mapToProjection reader)
            else
                return None
        }

    let upsertProjection (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (projection: RunProjection) : Task<unit> =
        ProjectionTx.upsertProjectionTx conn tx projection

    let updateProjection (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (candidate: JournalCandidate) (position: JournalPosition) (event: ValidatedEvent) : Task<RunProjection option> =
        task {
            if not (JournalDecision.isKnownEventType (EventType.value candidate.EventType)) then
                return None
            else
                let! existingOpt = loadProjection conn tx candidate.RunId
                match existingOpt with
                | None ->
                    let newProjectionOpt = RunProjection.applyEvent None position event
                    match newProjectionOpt with
                    | Some newProjection ->
                        do! upsertProjection conn tx newProjection
                        return Some newProjection
                    | None -> return None
                | Some existing ->
                    match RunProjection.applyEvent (Some existing) position event with
                    | Some updated ->
                        do! upsertProjection conn tx updated
                        return Some updated
                    | None -> return None
        }

    let classifyCollision (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (candidate: JournalCandidate) : Task<Result<JournalAppendOutcome, PersistenceFailure>> =
        task {
            let id = candidate.Identity
            let sp = candidate.StreamPosition
            let! byIdentity = JournalSqlExec.lookupByIdentity conn tx (EventSource.value id.Source) (EventId.value id.EventId)
            let! bySequence = JournalSqlExec.lookupByStreamPosition conn tx (InstanceId.value sp.InstanceId) (EpochId.value sp.EpochId) (EventSequence.value sp.Sequence)
            
            match byIdentity, bySequence with
            | None, None ->
                return Error ConstraintClassificationFailed
                
            | Some identityEntry, None ->
                // Identity exists but sequence doesn't - always conflict
                return Ok(EventIdentityConflict identityEntry.JournalPosition)
                
            | None, Some sequenceEntry ->
                // Sequence exists but identity doesn't - sequence conflict
                return Ok(SequenceConflict sequenceEntry.JournalPosition)
                
            | Some identityEntry, Some sequenceEntry
                when identityEntry.JournalPosition <> sequenceEntry.JournalPosition ->
                // Different rows - cross-identity conflict
                return Ok(CrossIdentityConflict(identityEntry.JournalPosition, sequenceEntry.JournalPosition))
                
            | Some identityEntry, Some _ ->
                // Same row - check envelope equality for replay detection
                let! envelopeEqual = JournalSqlExec.checkEnvelopeEqual conn tx (EventSource.value id.Source) (EventId.value id.EventId) candidate.EnvelopeJson
                return JournalDecision.classifyCollision byIdentity bySequence envelopeEqual
        }

    let execute (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (candidate: JournalCandidate) (event: ValidatedEvent) : Task<IngestionTxResult> =
        task {
            let! positionOpt = JournalSqlExec.tryInsert conn tx candidate
            match positionOpt with
            | Some pos ->
                let! projectionOpt = updateProjection conn tx candidate (JournalPosition pos) event
                return AppendSucceeded(Inserted (JournalPosition pos), projectionOpt)
            | None ->
                let! result = classifyCollision conn tx candidate
                match result with
                | Ok outcome -> return AppendSucceeded(outcome, None)
                | Error failure -> return AppendFailed failure
        }

    let create () : IngestionTransaction =
        { Execute = execute }

type IngestEventService =
    { Ingest: JournalCandidate -> ValidatedEvent -> Task<IngestionTxResult> }

module IngestEventService =

    let create (dataSource: NpgsqlDataSource) : IngestEventService =
        let ingest (candidate: JournalCandidate) (event: ValidatedEvent) : Task<IngestionTxResult> =
            let rec tryExecute (attemptNumber: int) : Async<IngestionTxResult> =
                async {
                    try
                        use conn = dataSource.CreateConnection()
                        do! conn.OpenAsync() |> Async.AwaitTask
                        use tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable)
                        try
                            let! result = IngestionTransaction.execute conn tx candidate event |> Async.AwaitTask
                            do! tx.CommitAsync() |> Async.AwaitTask
                            return result
                        with
                        | :? NpgsqlException as ex when ex.SqlState = SqlStates.SerializationFailure ->
                            do! tx.RollbackAsync() |> Async.AwaitTask
                            if attemptNumber < IngestionTransaction.maxTotalAttempts then
                                return! tryExecute (attemptNumber + 1)
                            else
                                return AppendFailed SerializationRetriesExhausted
                        | :? NpgsqlException as ex when
                            ex.SqlState = SqlStates.ConnectionException ||
                            ex.SqlState = SqlStates.ConnectionDoesNotExist ||
                            ex.SqlState = SqlStates.ConnectionFailure ->
                            do! tx.RollbackAsync() |> Async.AwaitTask
                            return AppendFailed DatabaseUnavailable
                        | ex ->
                            do! tx.RollbackAsync() |> Async.AwaitTask
                            let sqlState =
                                match ex with
                                | :? NpgsqlException as npgsql -> npgsql.SqlState
                                | _ -> "UNKNOWN"
                            return AppendFailed(UnexpectedDatabaseFailure sqlState)
                    with
                    | :? NpgsqlException as ex when
                        ex.SqlState = SqlStates.ConnectionException ||
                        ex.SqlState = SqlStates.ConnectionDoesNotExist ||
                        ex.SqlState = SqlStates.ConnectionFailure ->
                        return AppendFailed DatabaseUnavailable
                    | ex ->
                        let sqlState =
                            match ex with
                            | :? NpgsqlException as npgsql -> npgsql.SqlState
                            | _ -> "UNKNOWN"
                        return AppendFailed(UnexpectedDatabaseFailure sqlState)
                }
            tryExecute 1 |> Async.StartAsTask
        { Ingest = ingest }
