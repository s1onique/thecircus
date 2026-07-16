namespace Circus.Persistence.Postgres

open System
open System.Threading.Tasks
open Npgsql
open Circus.Application
open Circus.Application.JournalDecision
open Circus.Contracts
open Circus.Domain

/// Result internal to the persistence adapter.  It becomes an
/// `IngestEventResult` only after the complete transaction has committed.
type IngestionTxResult =
    | AppendSucceeded of JournalAppendOutcome * RunProjection option
    | AppendFailed of PersistenceFailure

/// Narrow observer seam used by tests to observe transaction boundaries
/// from outside the service.  Production composition uses `AttemptObserver.noop`.
type AttemptObserver =
    { ConnectionOpened: NpgsqlConnection -> unit
      TransactionBegun: NpgsqlConnection -> NpgsqlTransaction -> unit
      BeforeContestedMutation: NpgsqlConnection -> NpgsqlTransaction -> unit }

module AttemptObserver =
    let noop =
        { ConnectionOpened = fun _ -> ()
          TransactionBegun = fun _ _ -> ()
          BeforeContestedMutation = fun _ _ -> () }

type IngestionTransaction =
    { Execute:
        AttemptObserver
            -> NpgsqlConnection
            -> NpgsqlTransaction
            -> JournalCandidate
            -> ValidatedEvent
            -> Task<IngestionTxResult> }

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
        (observer: AttemptObserver)
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
                observer.BeforeContestedMutation conn tx
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
        (observer: AttemptObserver)
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (candidate: JournalCandidate)
        (event: ValidatedEvent)
        : Task<IngestionTxResult> =
        task {
            observer.BeforeContestedMutation conn tx
            let! position = JournalSqlExec.tryInsert conn tx candidate

            match position with
            | Some value ->
                let! projection = updateProjection observer conn tx candidate (JournalPosition value) event

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
    let private maximumAttempts = 3
    let private baseDelayMilliseconds = 25

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

    /// One attempt of the full transaction.  Opens a fresh connection,
    /// begins a serialisable transaction, runs the ingestion transaction,
    /// commits only on authoritative success, and rolls back otherwise.
    /// The returned `RetryOperationResult` is the sole authority for
    /// whether the policy retries.  Every NpgsqlException raised from
    /// the observer, the open, the begin, the work, or the commit is
    /// classified by this single try/catch so the policy always sees a
    /// consistent result.
    let private runOneAttempt
        (observer: AttemptObserver)
        (dataSource: NpgsqlDataSource)
        (candidate: JournalCandidate)
        (event: ValidatedEvent)
        : Task<RetryOperationResult<JournalAppendOutcome * RunProjection option>> =
        task {
            try
                use conn = dataSource.CreateConnection()

                try
                    do! conn.OpenAsync() |> Async.AwaitTask
                    observer.ConnectionOpened conn
                    use tx = conn.BeginTransaction(System.Data.IsolationLevel.Serializable)

                    try
                        observer.TransactionBegun conn tx

                        let! txResult = IngestionTransaction.execute observer conn tx candidate event

                        match txResult with
                        | AppendSucceeded(outcome, projection) ->
                            do! tx.CommitAsync() |> Async.AwaitTask
                            return RetrySucceeded(outcome, projection)
                        | AppendFailed failure ->
                            do! safeRollback tx
                            return PermanentFailure failure
                    with
                    | :? OperationCanceledException as cancellation ->
                        do! safeRollback tx
                        return raise cancellation
                    | :? NpgsqlException as ex when isRetryable ex ->
                        do! safeRollback tx
                        return RetryableFailure
                    | :? NpgsqlException as ex when isUnavailable ex ->
                        do! safeRollback tx
                        return PermanentFailure DatabaseUnavailable
                    | :? NpgsqlException as ex ->
                        do! safeRollback tx

                        return
                            PermanentFailure(
                                UnexpectedDatabaseFailure(
                                    ex.SqlState |> Option.ofObj |> Option.defaultValue "database_error"
                                )
                            )
                with
                | :? OperationCanceledException as cancellation -> return raise cancellation
                | :? NpgsqlException as ex when isRetryable ex -> return RetryableFailure
                | :? NpgsqlException as ex when isUnavailable ex -> return PermanentFailure DatabaseUnavailable
                | :? NpgsqlException as ex ->
                    return
                        PermanentFailure(
                            UnexpectedDatabaseFailure(
                                ex.SqlState |> Option.ofObj |> Option.defaultValue "database_error"
                            )
                        )
            with
            | :? OperationCanceledException as cancellation -> return raise cancellation
            | :? NpgsqlException as ex when isUnavailable ex -> return PermanentFailure DatabaseUnavailable
            | :? NpgsqlException as ex ->
                return
                    PermanentFailure(
                        UnexpectedDatabaseFailure(ex.SqlState |> Option.ofObj |> Option.defaultValue "database_error")
                    )
        }

    let private productionDelay (attempt: int) : Task<unit> =
        let work: Async<unit> =
            async { do! Async.AwaitTask(Task.Delay(baseDelayMilliseconds * attempt)) }

        Async.StartAsTask work

    /// Build a service that uses the production retry authority.  Tests pass
    /// the optional delay to make retry behaviour deterministic; the observer
    /// seam lets concurrency tests observe the real transaction boundary.
    let createWithPolicy
        (dataSource: NpgsqlDataSource)
        (observer: AttemptObserver)
        (delay: int -> Task<unit>)
        : Circus.Application.IngestEventService =
        let ingestion = RetryPolicy.execute maximumAttempts delay

        { Ingest =
            fun request ->
                task {
                    let candidate = IngestEvent.buildCandidate request

                    let! result = ingestion (fun _attempt -> runOneAttempt observer dataSource candidate request.Event)

                    return
                        match result with
                        | Ok(outcome, projection) -> Success(outcome, projection)
                        | Error failure -> PersistenceFailure failure
                } }

    let create (dataSource: NpgsqlDataSource) =
        createWithPolicy dataSource AttemptObserver.noop productionDelay

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
