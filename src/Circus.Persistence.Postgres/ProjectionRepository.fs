namespace Circus.Persistence.Postgres

open System
open System.Data.Common
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open Circus.Application
open Circus.Domain

module ProjectionSql =
    let upsertProjection =
        """
        INSERT INTO circus.circus_run_projection
            (run_id, state, started_journal_position, finished_journal_position,
             repository_ref, act_id, leamas_version, git_revision, started_by, started_at,
             outcome, finished_at, duration_ms, summary,
             checks_passed, checks_failed, checks_skipped,
             first_journal_position, last_journal_position, conflict_count, version)
        VALUES
            (@runId, @state, @startedJournalPosition, @finishedJournalPosition,
             @repositoryRef, @actId, @leamasVersion, @gitRevision, @startedBy, @startedAt,
             @outcome, @finishedAt, @durationMs, @summary,
             @checksPassed, @checksFailed, @checksSkipped,
             @firstJournalPosition, @lastJournalPosition, @conflictCount, @version)
        ON CONFLICT (run_id) DO UPDATE SET
            state = EXCLUDED.state,
            started_journal_position = EXCLUDED.started_journal_position,
            finished_journal_position = EXCLUDED.finished_journal_position,
            repository_ref = EXCLUDED.repository_ref,
            act_id = EXCLUDED.act_id,
            leamas_version = EXCLUDED.leamas_version,
            git_revision = EXCLUDED.git_revision,
            started_by = EXCLUDED.started_by,
            started_at = EXCLUDED.started_at,
            outcome = EXCLUDED.outcome,
            finished_at = EXCLUDED.finished_at,
            duration_ms = EXCLUDED.duration_ms,
            summary = EXCLUDED.summary,
            checks_passed = EXCLUDED.checks_passed,
            checks_failed = EXCLUDED.checks_failed,
            checks_skipped = EXCLUDED.checks_skipped,
            last_journal_position = EXCLUDED.last_journal_position,
            conflict_count = EXCLUDED.conflict_count,
            version = EXCLUDED.version;
        """

    let selectByRunId =
        """
        SELECT run_id, state,
               started_journal_position, finished_journal_position,
               repository_ref, act_id, leamas_version, git_revision, started_by, started_at,
               outcome, finished_at, duration_ms, summary,
               checks_passed, checks_failed, checks_skipped,
               first_journal_position, last_journal_position, conflict_count, version
        FROM circus.circus_run_projection
        WHERE run_id = @runId;
        """

    let selectAll =
        """
        SELECT run_id, state,
               started_journal_position, finished_journal_position,
               repository_ref, act_id, leamas_version, git_revision, started_by, started_at,
               outcome, finished_at, duration_ms, summary,
               checks_passed, checks_failed, checks_skipped,
               first_journal_position, last_journal_position, conflict_count, version
        FROM circus.circus_run_projection
        ORDER BY first_journal_position ASC;
        """

module PersistenceHelpers =
    let toDbValue (value: 'a option) : obj =
        value |> Option.map box |> Option.defaultValue DBNull.Value

    let toDbTimestamp (value: DateTimeOffset option) : obj =
        value
        |> Option.map (fun dt -> dt.UtcDateTime :> obj)
        |> Option.defaultValue DBNull.Value

    let toOutcomeDb (outcome: ExecutionOutcome option) : obj =
        outcome
        |> Option.map ExecutionOutcome.toWire
        |> Option.map box
        |> Option.defaultValue DBNull.Value

type ProjectionRepository =
    { Upsert: RunProjection -> Task<unit>
      GetByRunId: RunId -> Task<Result<RunProjection option, PersistenceFailure>>
      GetAll: unit -> Task<Result<RunProjection list, PersistenceFailure>> }

module ProjectionRepository =
    let private invariantFailure: Result<RunProjection, PersistenceFailure> =
        Error ProjectionInvariantFailed

    let private mapState (state: string) : Result<RunProjectionState, PersistenceFailure> =
        match state with
        | "StartedOnly" -> Ok StartedOnly
        | "FinishedWithoutStart" -> Ok FinishedWithoutStart
        | "Completed" -> Ok Completed
        | "Conflicted" -> Ok Conflicted
        | _ -> Error ProjectionInvariantFailed

    let toStateString state =
        match state with
        | StartedOnly -> "StartedOnly"
        | FinishedWithoutStart -> "FinishedWithoutStart"
        | Completed -> "Completed"
        | Conflicted -> "Conflicted"

    let private mapOutcome (outcome: string) : Result<ExecutionOutcome, PersistenceFailure> =
        match ExecutionOutcome.tryFromWire outcome with
        | Some value -> Ok value
        | None -> Error ProjectionInvariantFailed

    /// Strictly decode one persisted row.  Every column is resolved by name so
    /// a changed SELECT list cannot silently shift an authority into another
    /// field.  Invalid data is reported as a typed invariant failure; no
    /// default enum, empty identifier, zero count, or current timestamp is
    /// manufactured.
    let mapToProjection (reader: DbDataReader) : Result<RunProjection, PersistenceFailure> =
        let invalid () = raise (InvalidCastException())

        let decode () =
            let ordinal name = reader.GetOrdinal name

            let requiredString name =
                let index = ordinal name

                if reader.IsDBNull index then
                    invalid ()

                reader.GetString index

            let optionalString name =
                let index = ordinal name

                if reader.IsDBNull index then
                    None
                else
                    Some(reader.GetString index)

            let requiredInt64 name =
                let index = ordinal name

                if reader.IsDBNull index then
                    invalid ()

                reader.GetInt64 index

            let optionalInt64 name =
                let index = ordinal name

                if reader.IsDBNull index then
                    None
                else
                    Some(reader.GetInt64 index)

            let requiredInt32 name =
                let index = ordinal name

                if reader.IsDBNull index then
                    invalid ()

                reader.GetInt32 index

            let optionalInt32 name =
                let index = ordinal name

                if reader.IsDBNull index then
                    None
                else
                    Some(reader.GetInt32 index)

            let optionalTimestamp name =
                let index = ordinal name

                if reader.IsDBNull index then
                    None
                else
                    let value = reader.GetDateTime index
                    Some(DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))

            let runId =
                match RunId.tryCreate (reader.GetGuid(ordinal "run_id")) with
                | Some value -> value
                | None -> invalid ()

            let state =
                match mapState (requiredString "state") with
                | Ok value -> value
                | Error _ -> invalid ()

            let startedEvent =
                optionalInt64 "started_journal_position" |> Option.map JournalPosition

            let finishedEvent =
                optionalInt64 "finished_journal_position" |> Option.map JournalPosition

            let firstPosition = JournalPosition(requiredInt64 "first_journal_position")
            let lastPosition = JournalPosition(requiredInt64 "last_journal_position")
            let conflictCount = requiredInt32 "conflict_count"
            let version = requiredInt64 "version"

            let repository =
                optionalString "repository_ref"
                |> Option.map (fun value -> RepositoryRef.tryCreate value |> Option.defaultWith invalid)

            let actId =
                optionalString "act_id"
                |> Option.map (fun value -> ActId.tryCreate value |> Option.defaultWith invalid)

            let leamasVersion =
                optionalString "leamas_version"
                |> Option.map (fun value -> LeamasVersion.tryCreate value |> Option.defaultWith invalid)

            let outcome =
                optionalString "outcome"
                |> Option.map (fun value ->
                    match mapOutcome value with
                    | Ok mapped -> mapped
                    | Error _ -> invalid ())

            let checks =
                match optionalInt32 "checks_passed", optionalInt32 "checks_failed", optionalInt32 "checks_skipped" with
                | None, None, None -> None
                | Some passed, Some failed, Some skipped ->
                    Some(CheckCounts.tryCreate passed failed skipped |> Option.defaultWith invalid)
                | _ -> invalid ()

            let duration = optionalInt64 "duration_ms"

            match duration with
            | Some value when value < 0L -> invalid ()
            | _ -> ()

            let startedAt = optionalTimestamp "started_at"
            let finishedAt = optionalTimestamp "finished_at"
            let summary = optionalString "summary"
            let startedBy = optionalString "started_by"
            let gitRevision = optionalString "git_revision"

            let hasStarted = startedEvent.IsSome
            let hasFinished = finishedEvent.IsSome

            let startedAuthorityComplete =
                hasStarted && repository.IsSome && leamasVersion.IsSome && startedAt.IsSome

            let startedAuthorityAbsent =
                not hasStarted
                && repository.IsNone
                && actId.IsNone
                && leamasVersion.IsNone
                && gitRevision.IsNone
                && startedBy.IsNone
                && startedAt.IsNone

            let finishedAuthorityComplete =
                hasFinished
                && outcome.IsSome
                && finishedAt.IsSome
                && duration.IsSome
                && checks.IsSome

            let finishedAuthorityAbsent =
                not hasFinished
                && outcome.IsNone
                && finishedAt.IsNone
                && duration.IsNone
                && summary.IsNone
                && checks.IsNone

            let validPositions =
                JournalPosition.value firstPosition >= 1L
                && JournalPosition.value lastPosition >= JournalPosition.value firstPosition
                && (startedEvent
                    |> Option.forall (fun p ->
                        JournalPosition.value p >= 1L
                        && JournalPosition.value p <= JournalPosition.value lastPosition))
                && (finishedEvent
                    |> Option.forall (fun p ->
                        JournalPosition.value p >= 1L
                        && JournalPosition.value p <= JournalPosition.value lastPosition))

            let validState =
                match state with
                | StartedOnly -> hasStarted && not hasFinished && conflictCount = 0
                | FinishedWithoutStart -> not hasStarted && hasFinished && conflictCount = 0
                | Completed -> hasStarted && hasFinished && conflictCount = 0
                | Conflicted -> conflictCount > 0 && (hasStarted || hasFinished)

            if
                version < 1L
                || conflictCount < 0
                || not validPositions
                || not startedAuthorityComplete && not startedAuthorityAbsent
                || not finishedAuthorityComplete && not finishedAuthorityAbsent
                || not validState
            then
                invalid ()

            { RunId = runId
              State = state
              StartedEvent = startedEvent
              FinishedEvent = finishedEvent
              Repository = repository
              ActId = actId
              LeamasVersion = leamasVersion
              GitRevision = gitRevision
              StartedBy = startedBy
              StartedAt = startedAt
              Outcome = outcome
              FinishedAt = finishedAt
              DurationMilliseconds = duration
              Summary = summary
              Checks = checks
              FirstJournalPosition = firstPosition
              LastJournalPosition = lastPosition
              ConflictCount = conflictCount
              Version = version }

        try
            Ok(decode ())
        with
        | :? InvalidCastException
        | :? FormatException
        | :? IndexOutOfRangeException
        | :? ArgumentException
        | :? OverflowException -> invariantFailure

    let addProjectionParameters (cmd: NpgsqlCommand) (projection: RunProjection) =
        let add name dbType value =
            cmd.Parameters.AddWithValue(name, dbType, value) |> ignore

        add "runId" NpgsqlDbType.Uuid (RunId.value projection.RunId)
        add "state" NpgsqlDbType.Text (toStateString projection.State)

        add
            "startedJournalPosition"
            NpgsqlDbType.Bigint
            (projection.StartedEvent
             |> Option.map JournalPosition.value
             |> PersistenceHelpers.toDbValue)

        add
            "finishedJournalPosition"
            NpgsqlDbType.Bigint
            (projection.FinishedEvent
             |> Option.map JournalPosition.value
             |> PersistenceHelpers.toDbValue)

        add
            "repositoryRef"
            NpgsqlDbType.Text
            (projection.Repository
             |> Option.map RepositoryRef.value
             |> PersistenceHelpers.toDbValue)

        add "actId" NpgsqlDbType.Text (projection.ActId |> Option.map ActId.value |> PersistenceHelpers.toDbValue)

        add
            "leamasVersion"
            NpgsqlDbType.Text
            (projection.LeamasVersion
             |> Option.map LeamasVersion.value
             |> PersistenceHelpers.toDbValue)

        add "gitRevision" NpgsqlDbType.Text (PersistenceHelpers.toDbValue projection.GitRevision)
        add "startedBy" NpgsqlDbType.Text (PersistenceHelpers.toDbValue projection.StartedBy)
        add "startedAt" NpgsqlDbType.TimestampTz (PersistenceHelpers.toDbTimestamp projection.StartedAt)
        add "outcome" NpgsqlDbType.Text (PersistenceHelpers.toOutcomeDb projection.Outcome)
        add "finishedAt" NpgsqlDbType.TimestampTz (PersistenceHelpers.toDbTimestamp projection.FinishedAt)
        add "durationMs" NpgsqlDbType.Bigint (PersistenceHelpers.toDbValue projection.DurationMilliseconds)
        add "summary" NpgsqlDbType.Text (PersistenceHelpers.toDbValue projection.Summary)

        add
            "checksPassed"
            NpgsqlDbType.Integer
            (projection.Checks
             |> Option.map (fun c -> c.Passed)
             |> PersistenceHelpers.toDbValue)

        add
            "checksFailed"
            NpgsqlDbType.Integer
            (projection.Checks
             |> Option.map (fun c -> c.Failed)
             |> PersistenceHelpers.toDbValue)

        add
            "checksSkipped"
            NpgsqlDbType.Integer
            (projection.Checks
             |> Option.map (fun c -> c.Skipped)
             |> PersistenceHelpers.toDbValue)

        add "firstJournalPosition" NpgsqlDbType.Bigint (JournalPosition.value projection.FirstJournalPosition)
        add "lastJournalPosition" NpgsqlDbType.Bigint (JournalPosition.value projection.LastJournalPosition)
        add "conflictCount" NpgsqlDbType.Integer projection.ConflictCount
        add "version" NpgsqlDbType.Bigint projection.Version

    let create (dataSource: NpgsqlDataSource) : ProjectionRepository =
        let upsert projection =
            task {
                use cmd = dataSource.CreateCommand(ProjectionSql.upsertProjection)
                addProjectionParameters cmd projection
                do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
            }

        let getByRunId runId : Task<Result<RunProjection option, PersistenceFailure>> =
            task {
                use cmd = dataSource.CreateCommand(ProjectionSql.selectByRunId)

                cmd.Parameters.AddWithValue("runId", NpgsqlDbType.Uuid, RunId.value runId)
                |> ignore

                use! reader = cmd.ExecuteReaderAsync()

                if reader.Read() then
                    return mapToProjection reader |> Result.map Some
                else
                    return Ok None
            }

        let getAll () : Task<Result<RunProjection list, PersistenceFailure>> =
            task {
                use cmd = dataSource.CreateCommand(ProjectionSql.selectAll)
                use! reader = cmd.ExecuteReaderAsync()
                let mutable values = []
                let mutable failure = None

                while reader.Read() && failure.IsNone do
                    match mapToProjection reader with
                    | Ok value -> values <- value :: values
                    | Error error -> failure <- Some error

                return
                    match failure with
                    | Some error -> Error error
                    | None -> Ok(List.rev values)
            }

        { Upsert = upsert
          GetByRunId = getByRunId
          GetAll = getAll }

module ProjectionTx =
    let upsertProjectionTx (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (projection: RunProjection) : Task<unit> =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- ProjectionSql.upsertProjection
            ProjectionRepository.addProjectionParameters cmd projection
            do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
        }
