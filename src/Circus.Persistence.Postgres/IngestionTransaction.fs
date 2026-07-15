namespace Circus.Persistence.Postgres

open System
open System.Threading.Tasks
open Npgsql
open Circus.Application
open Circus.Application.JournalDecision
open Circus.Contracts
open Circus.Domain

/// Result internal to the persistence adapter.  It becomes an
/// IngestEventResult only after the complete transaction has committed.
type IngestionTxResult =
    | AppendSucceeded of JournalAppendOutcome * RunProjection option
    | AppendFailed of PersistenceFailure

type IngestionTransaction =
    { Execute: NpgsqlConnection -> NpgsqlTransaction -> JournalCandidate -> ValidatedEvent -> Task<IngestionTxResult> }

module IngestionTransaction =
    let private loadProjection
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (runId: RunId)
        : Task<Result<RunProjection option, PersistenceFailure>> =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- ProjectionSql.selectByRunId

            cmd.Parameters.AddWithValue("runId", NpgsqlTypes.NpgsqlDbType.Uuid, RunId.value runId)
            |> ignore

            use! reader = cmd.ExecuteReaderAsync()

            if reader.Read() then
                return ProjectionRepository.mapToProjection reader |> Result.map Some
            else
                return Ok None
        }

    let private updateProjection
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (candidate: JournalCandidate)
        (position: JournalPosition)
        (event: ValidatedEvent)
        : Task<Result<RunProjection option, PersistenceFailure>> =
        task {
            if not (JournalDecision.isKnownEventType (EventType.value candidate.EventType)) then
                return Ok None
            else
                let! existing = loadProjection conn tx candidate.RunId

                match existing with
                | Error failure -> return Error failure
                | Ok current ->
                    let updated = RunProjection.applyEvent current position event

                    match updated with
                    | None -> return Ok None
                    | Some projection ->
                        do! ProjectionTx.upsertProjectionTx conn tx projection
                        return Ok(Some projection)
        }

    let private classifyCollision
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (candidate: JournalCandidate)
        : Task<Result<JournalAppendOutcome, PersistenceFailure>> =
        task {
            let! byIdentity =
                JournalSqlExec.lookupByIdentity
                    conn
                    tx
                    (EventSource.value candidate.Identity.Source)
                    (EventId.value candidate.Identity.EventId)

            let! bySequence =
                JournalSqlExec.lookupByStreamPosition
                    conn
                    tx
                    (InstanceId.value candidate.StreamPosition.InstanceId)
                    (EpochId.value candidate.StreamPosition.EpochId)
                    (EventSequence.value candidate.StreamPosition.Sequence)

            match byIdentity, bySequence with
            | None, None -> return Error ConstraintClassificationFailed
            | Some identityEntry, None -> return Ok(EventIdentityConflict identityEntry.JournalPosition)
            | None, Some sequenceEntry -> return Ok(SequenceConflict sequenceEntry.JournalPosition)
            | Some identityEntry, Some sequenceEntry when identityEntry.JournalPosition <> sequenceEntry.JournalPosition ->
                return Ok(CrossIdentityConflict(identityEntry.JournalPosition, sequenceEntry.JournalPosition))
            | Some identityEntry, Some _ ->
                let! semanticEqual =
                    JournalSqlExec.checkEnvelopeEqual
                        conn
                        tx
                        (EventSource.value candidate.Identity.Source)
                        (EventId.value candidate.Identity.EventId)
                        candidate.EnvelopeJson

                return JournalDecision.classifyCollision byIdentity bySequence semanticEqual
        }

    let execute
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (candidate: JournalCandidate)
        (event: ValidatedEvent)
        : Task<IngestionTxResult> =
        task {
            let! position = JournalSqlExec.tryInsert conn tx candidate

            match position with
            | Some value ->
                let! projection = updateProjection conn tx candidate (JournalPosition value) event

                match projection with
                | Ok projection -> return AppendSucceeded(Inserted(JournalPosition value), projection)
                | Error failure -> return AppendFailed failure
            | None ->
                let! collision = classifyCollision conn tx candidate

                match collision with
                | Ok outcome -> return AppendSucceeded(outcome, None)
                | Error failure -> return AppendFailed failure
        }

    let create () = { Execute = execute }

/// A fresh connection and transaction are obtained for every attempt.  The
/// injected delay makes retry tests deterministic without changing the
/// production transaction algorithm.
module IngestEventService =
    let private isRetryable (ex: NpgsqlException) =
        ex.SqlState = SqlStates.SerializationFailure
        || ex.SqlState = SqlStates.DeadlockDetected

    let private isUnavailable (ex: NpgsqlException) =
        ex.SqlState = SqlStates.ConnectionException
        || ex.SqlState = SqlStates.ConnectionDoesNotExist
        || ex.SqlState = SqlStates.ConnectionFailure

    let private safeRollback (tx: NpgsqlTransaction) =
        task {
            try
                do! tx.RollbackAsync() |> Async.AwaitTask
            with :? NpgsqlException ->
                ()
        }

    let createWithPolicy
        (dataSource: NpgsqlDataSource)
        (maximumAttempts: int)
        (delay: int -> Task<unit>)
        : Circus.Application.IngestEventService =
        if maximumAttempts < 1 then
            invalidArg "maximumAttempts" "must be at least one"

        let rec executeAttempt
            (attempt: int)
            (candidate: JournalCandidate)
            (event: ValidatedEvent)
            : Task<IngestEventResult> =
            task {
                try
                    use conn = dataSource.CreateConnection()
                    do! conn.OpenAsync() |> Async.AwaitTask
                    use tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable)

                    try
                        let! txResult = IngestionTransaction.execute conn tx candidate event
                        do! tx.CommitAsync() |> Async.AwaitTask

                        return
                            match txResult with
                            | AppendSucceeded(outcome, projection) -> Success(outcome, projection)
                            | AppendFailed failure -> PersistenceFailure failure
                    with
                    | :? OperationCanceledException as cancellation ->
                        do! safeRollback tx
                        return raise cancellation
                    | :? NpgsqlException as ex when isRetryable ex ->
                        do! safeRollback tx

                        if attempt < maximumAttempts then
                            do! delay attempt
                            return! executeAttempt (attempt + 1) candidate event
                        else
                            return PersistenceFailure SerializationRetriesExhausted
                    | :? NpgsqlException as ex when isUnavailable ex ->
                        do! safeRollback tx
                        return PersistenceFailure DatabaseUnavailable
                    | :? NpgsqlException as ex ->
                        do! safeRollback tx

                        return
                            PersistenceFailure(
                                UnexpectedDatabaseFailure(
                                    ex.SqlState |> Option.ofObj |> Option.defaultValue "database_error"
                                )
                            )
                with
                | :? OperationCanceledException as cancellation -> return raise cancellation
                | :? NpgsqlException as ex when isUnavailable ex -> return PersistenceFailure DatabaseUnavailable
                | :? NpgsqlException as ex ->
                    return
                        PersistenceFailure(
                            UnexpectedDatabaseFailure(
                                ex.SqlState |> Option.ofObj |> Option.defaultValue "database_error"
                            )
                        )
            }

        { Ingest =
            fun request ->
                let candidate = IngestEvent.buildCandidate request
                executeAttempt 1 candidate request.Event }

    let private productionDelay (attempt: int) : Task<unit> =
        let work: Async<unit> = async { do! Async.AwaitTask(Task.Delay(25 * attempt)) }
        Async.StartAsTask work

    let create (dataSource: NpgsqlDataSource) =
        createWithPolicy dataSource 3 productionDelay

/// Rebuild reads only durable raw authority, decodes it through the contract
/// decoder, and folds it through the same RunProjection.applyEvent reducer as
/// online ingestion.  A corrupt journal row is therefore never fabricated
/// into a plausible domain event.
module ProjectionRebuild =
    let rebuildFromJournal
        (dataSource: NpgsqlDataSource)
        : Task<Result<Map<RunId, RunProjection>, PersistenceFailure>> =
        task {
            try
                let! entries = JournalSqlExec.lookupAllOrdered dataSource

                let decoded =
                    entries
                    |> List.fold
                        (fun state entry ->
                            match state with
                            | Error _ -> state
                            | Ok values ->
                                match
                                    EventDecoder.decode EventDecoder.DefaultMaximumBytes (ReadOnlyMemory entry.RawBody)
                                with
                                | Ok event -> Ok((entry.JournalPosition, event) :: values)
                                | Error _ -> Error ProjectionInvariantFailed)
                        (Ok [])

                match decoded with
                | Error failure -> return Error failure
                | Ok values -> return Ok(RunProjection.rebuild (List.rev values))
            with
            | :? OperationCanceledException as cancellation -> return raise cancellation
            | :? NpgsqlException as ex ->
                return
                    Error(
                        UnexpectedDatabaseFailure(ex.SqlState |> Option.ofObj |> Option.defaultValue "database_error")
                    )
        }
