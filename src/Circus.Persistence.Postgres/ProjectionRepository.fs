namespace Circus.Persistence.Postgres

open System
open System.Threading.Tasks
open System.Data.Common
open Npgsql
open NpgsqlTypes
open Circus.Application
open Circus.Domain

/// SQL statements for the run projection.
module ProjectionSql =

    let insertProjection =
        """
        INSERT INTO circus_run_projection
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
        """

    let updateProjection =
        """
        UPDATE circus_run_projection
        SET
            state = @state,
            started_journal_position = @startedJournalPosition,
            finished_journal_position = @finishedJournalPosition,
            repository_ref = @repositoryRef,
            act_id = @actId,
            leamas_version = @leamasVersion,
            git_revision = @gitRevision,
            started_by = @startedBy,
            started_at = @startedAt,
            outcome = @outcome,
            finished_at = @finishedAt,
            duration_ms = @durationMs,
            summary = @summary,
            checks_passed = @checksPassed,
            checks_failed = @checksFailed,
            checks_skipped = @checksSkipped,
            last_journal_position = @lastJournalPosition,
            conflict_count = @conflictCount,
            version = @version
        WHERE run_id = @runId
          AND version < @version
        """

    let upsertProjection =
        """
        INSERT INTO circus_run_projection
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
        ON CONFLICT (run_id) DO UPDATE
        SET
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
            version = EXCLUDED.version
        """

    let selectByRunId =
        """
        SELECT
            run_id, state,
            started_journal_position, finished_journal_position,
            repository_ref, act_id, leamas_version, git_revision, started_by, started_at,
            outcome, finished_at, duration_ms, summary,
            checks_passed, checks_failed, checks_skipped,
            first_journal_position, last_journal_position, conflict_count, version
        FROM circus_run_projection
        WHERE run_id = @runId
        """

    let selectAll =
        """
        SELECT
            run_id, state,
            started_journal_position, finished_journal_position,
            repository_ref, act_id, leamas_version, git_revision, started_by, started_at,
            outcome, finished_at, duration_ms, summary,
            checks_passed, checks_failed, checks_skipped,
            first_journal_position, last_journal_position, conflict_count, version
        FROM circus_run_projection
        ORDER BY first_journal_position ASC
        """

module PersistenceHelpers =

    /// Convert F# option to underlying value or DBNull.Value for Npgsql.
    let inline toDbValue (value: 'a option) : obj =
        value
        |> Option.map box
        |> Option.defaultValue DBNull.Value

    /// Convert DateTimeOffset option to nullable DateTime or DBNull.
    let inline toDbTimestamp (value: DateTimeOffset option) : obj =
        match value with
        | Some dt -> dt.UtcDateTime :> obj
        | None -> DBNull.Value

    /// Convert outcome option to string or DBNull.
    let inline toOutcomeDb (outcome: ExecutionOutcome option) : obj =
        match outcome with
        | Some o -> ExecutionOutcome.toWire o :> obj
        | None -> DBNull.Value

type ProjectionRepository =
    { Upsert: RunProjection -> Task<unit>
      GetByRunId: RunId -> Task<RunProjection option>
      GetAll: unit -> Task<RunProjection list> }

module ProjectionRepository =

    let private mapState (state: string) : RunProjectionState =
        match state with
        | "StartedOnly" -> StartedOnly
        | "FinishedWithoutStart" -> FinishedWithoutStart
        | "Completed" -> Completed
        | "Conflicted" -> Conflicted
        | _ -> Conflicted

    let toStateString (state: RunProjectionState) : string =
        match state with
        | StartedOnly -> "StartedOnly"
        | FinishedWithoutStart -> "FinishedWithoutStart"
        | Completed -> "Completed"
        | Conflicted -> "Conflicted"

    let private mapOutcome (outcome: string) : ExecutionOutcome =
        match outcome with
        | "succeeded" -> ExecutionOutcome.Succeeded
        | "failed" -> ExecutionOutcome.Failed
        | "cancelled" -> ExecutionOutcome.Cancelled
        | "timed_out" -> ExecutionOutcome.TimedOut
        | _ -> ExecutionOutcome.Failed

    let mapToProjection (reader: #DbDataReader) : RunProjection =
        let getStringOrNull (i: int) = if reader.IsDBNull(i) then None else Some(reader.GetString(i))
        let getInt64OrNull (i: int) = if reader.IsDBNull(i) then None else Some(reader.GetInt64(i))
        let getInt32OrNull (i: int) = if reader.IsDBNull(i) then None else Some(reader.GetInt32(i))
        let getDateTimeOffsetOrNull (i: int) = if reader.IsDBNull(i) then None else Some(DateTimeOffset(reader.GetDateTime(i)))

        let checks =
            let passed = getInt32OrNull 14
            let failed = getInt32OrNull 15
            let skipped = getInt32OrNull 16
            match passed, failed, skipped with
            | Some p, Some f, Some s -> Some { Passed = p; Failed = f; Skipped = s }
            | _ -> None

        { RunId = (RunId.tryCreate (reader.GetGuid(0))).Value
          State = mapState (reader.GetString(1))
          StartedEvent = getInt64OrNull 2 |> Option.map JournalPosition
          FinishedEvent = getInt64OrNull 3 |> Option.map JournalPosition
          Repository = getStringOrNull 4 |> Option.bind RepositoryRef.tryCreate
          ActId = getStringOrNull 5 |> Option.bind ActId.tryCreate
          LeamasVersion = getStringOrNull 6 |> Option.bind LeamasVersion.tryCreate
          GitRevision = getStringOrNull 7
          StartedBy = getStringOrNull 8
          StartedAt = getDateTimeOffsetOrNull 9
          Outcome = getStringOrNull 10 |> Option.map mapOutcome
          FinishedAt = getDateTimeOffsetOrNull 11
          DurationMilliseconds = getInt64OrNull 12
          Summary = getStringOrNull 13
          Checks = checks
          FirstJournalPosition = JournalPosition(reader.GetInt64(17))
          LastJournalPosition = JournalPosition(reader.GetInt64(18))
          ConflictCount = reader.GetInt32(19)
          Version = reader.GetInt64(20) }

    let create (dataSource: NpgsqlDataSource) : ProjectionRepository =

        let upsert (projection: RunProjection) : Task<unit> =
            task {
                use cmd = dataSource.CreateCommand(ProjectionSql.upsertProjection)

                cmd.Parameters.AddWithValue("runId", NpgsqlDbType.Uuid, RunId.value projection.RunId) |> ignore
                cmd.Parameters.AddWithValue("state", NpgsqlDbType.Text, toStateString projection.State) |> ignore
                cmd.Parameters.AddWithValue("startedJournalPosition", NpgsqlDbType.Bigint, projection.StartedEvent |> Option.map JournalPosition.value |> PersistenceHelpers.toDbValue) |> ignore
                cmd.Parameters.AddWithValue("finishedJournalPosition", NpgsqlDbType.Bigint, projection.FinishedEvent |> Option.map JournalPosition.value |> PersistenceHelpers.toDbValue) |> ignore
                cmd.Parameters.AddWithValue("repositoryRef", NpgsqlDbType.Text, projection.Repository |> Option.map RepositoryRef.value |> PersistenceHelpers.toDbValue) |> ignore
                cmd.Parameters.AddWithValue("actId", NpgsqlDbType.Text, projection.ActId |> Option.map ActId.value |> PersistenceHelpers.toDbValue) |> ignore
                cmd.Parameters.AddWithValue("leamasVersion", NpgsqlDbType.Text, projection.LeamasVersion |> Option.map LeamasVersion.value |> PersistenceHelpers.toDbValue) |> ignore
                cmd.Parameters.AddWithValue("gitRevision", NpgsqlDbType.Text, PersistenceHelpers.toDbValue projection.GitRevision) |> ignore
                cmd.Parameters.AddWithValue("startedBy", NpgsqlDbType.Text, PersistenceHelpers.toDbValue projection.StartedBy) |> ignore
                cmd.Parameters.AddWithValue("startedAt", NpgsqlDbType.TimestampTz, PersistenceHelpers.toDbTimestamp projection.StartedAt) |> ignore
                cmd.Parameters.AddWithValue("outcome", NpgsqlDbType.Text, PersistenceHelpers.toOutcomeDb projection.Outcome) |> ignore
                cmd.Parameters.AddWithValue("finishedAt", NpgsqlDbType.TimestampTz, PersistenceHelpers.toDbTimestamp projection.FinishedAt) |> ignore
                cmd.Parameters.AddWithValue("durationMs", NpgsqlDbType.Bigint, PersistenceHelpers.toDbValue projection.DurationMilliseconds) |> ignore
                cmd.Parameters.AddWithValue("summary", NpgsqlDbType.Text, PersistenceHelpers.toDbValue projection.Summary) |> ignore
                cmd.Parameters.AddWithValue("checksPassed", NpgsqlDbType.Integer, projection.Checks |> Option.map (fun c -> c.Passed) |> PersistenceHelpers.toDbValue) |> ignore
                cmd.Parameters.AddWithValue("checksFailed", NpgsqlDbType.Integer, projection.Checks |> Option.map (fun c -> c.Failed) |> PersistenceHelpers.toDbValue) |> ignore
                cmd.Parameters.AddWithValue("checksSkipped", NpgsqlDbType.Integer, projection.Checks |> Option.map (fun c -> c.Skipped) |> PersistenceHelpers.toDbValue) |> ignore
                cmd.Parameters.AddWithValue("firstJournalPosition", NpgsqlDbType.Bigint, JournalPosition.value projection.FirstJournalPosition) |> ignore
                cmd.Parameters.AddWithValue("lastJournalPosition", NpgsqlDbType.Bigint, JournalPosition.value projection.LastJournalPosition) |> ignore
                cmd.Parameters.AddWithValue("conflictCount", NpgsqlDbType.Integer, projection.ConflictCount) |> ignore
                cmd.Parameters.AddWithValue("version", NpgsqlDbType.Bigint, projection.Version) |> ignore

                do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
            }

        let getByRunId (runId: RunId) : Task<RunProjection option> =
            task {
                use cmd = dataSource.CreateCommand(ProjectionSql.selectByRunId)
                cmd.Parameters.AddWithValue("runId", NpgsqlDbType.Uuid, RunId.value runId) |> ignore

                use! reader = cmd.ExecuteReaderAsync()
                if reader.Read() then
                    return Some(mapToProjection reader)
                else
                    return None
            }

        let getAll () : Task<RunProjection list> =
            task {
                use cmd = dataSource.CreateCommand(ProjectionSql.selectAll)

                let mutable results = []
                use! reader = cmd.ExecuteReaderAsync()
                while reader.Read() do
                    results <- (mapToProjection reader) :: results

                return List.rev results
            }

        { Upsert = upsert
          GetByRunId = getByRunId
          GetAll = getAll }

// Transaction-aware upsert for use within ingestion transactions
module ProjectionTx =

    let upsertProjectionTx (conn: Npgsql.NpgsqlConnection) (tx: Npgsql.NpgsqlTransaction) (projection: RunProjection) : Task<unit> =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- ProjectionSql.upsertProjection

            let addParam name dbType value = cmd.Parameters.AddWithValue(name, dbType, value) |> ignore
            addParam "runId" NpgsqlDbType.Uuid (RunId.value projection.RunId)
            addParam "state" NpgsqlDbType.Text (ProjectionRepository.toStateString projection.State)
            addParam "startedJournalPosition" NpgsqlDbType.Bigint (projection.StartedEvent |> Option.map JournalPosition.value |> PersistenceHelpers.toDbValue)
            addParam "finishedJournalPosition" NpgsqlDbType.Bigint (projection.FinishedEvent |> Option.map JournalPosition.value |> PersistenceHelpers.toDbValue)
            addParam "repositoryRef" NpgsqlDbType.Text (projection.Repository |> Option.map RepositoryRef.value |> PersistenceHelpers.toDbValue)
            addParam "actId" NpgsqlDbType.Text (projection.ActId |> Option.map ActId.value |> PersistenceHelpers.toDbValue)
            addParam "leamasVersion" NpgsqlDbType.Text (projection.LeamasVersion |> Option.map LeamasVersion.value |> PersistenceHelpers.toDbValue)
            addParam "gitRevision" NpgsqlDbType.Text (PersistenceHelpers.toDbValue projection.GitRevision)
            addParam "startedBy" NpgsqlDbType.Text (PersistenceHelpers.toDbValue projection.StartedBy)
            addParam "startedAt" NpgsqlDbType.TimestampTz (PersistenceHelpers.toDbTimestamp projection.StartedAt)
            addParam "outcome" NpgsqlDbType.Text (PersistenceHelpers.toOutcomeDb projection.Outcome)
            addParam "finishedAt" NpgsqlDbType.TimestampTz (PersistenceHelpers.toDbTimestamp projection.FinishedAt)
            addParam "durationMs" NpgsqlDbType.Bigint (PersistenceHelpers.toDbValue projection.DurationMilliseconds)
            addParam "summary" NpgsqlDbType.Text (PersistenceHelpers.toDbValue projection.Summary)
            addParam "checksPassed" NpgsqlDbType.Integer (projection.Checks |> Option.map (fun c -> c.Passed) |> PersistenceHelpers.toDbValue)
            addParam "checksFailed" NpgsqlDbType.Integer (projection.Checks |> Option.map (fun c -> c.Failed) |> PersistenceHelpers.toDbValue)
            addParam "checksSkipped" NpgsqlDbType.Integer (projection.Checks |> Option.map (fun c -> c.Skipped) |> PersistenceHelpers.toDbValue)
            addParam "firstJournalPosition" NpgsqlDbType.Bigint (JournalPosition.value projection.FirstJournalPosition)
            addParam "lastJournalPosition" NpgsqlDbType.Bigint (JournalPosition.value projection.LastJournalPosition)
            addParam "conflictCount" NpgsqlDbType.Integer projection.ConflictCount
            addParam "version" NpgsqlDbType.Bigint projection.Version

            do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
        }
