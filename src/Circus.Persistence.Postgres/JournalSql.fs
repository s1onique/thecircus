namespace Circus.Persistence.Postgres

open System
open System.Security.Cryptography
open System.Threading.Tasks
open System.Data.Common
open Npgsql
open NpgsqlTypes
open Circus.Application
open Circus.Domain

/// All application SQL names the dedicated circus schema explicitly.  No
/// command depends on the connection's search_path.
module JournalSql =
    let insertJournal =
        """
        INSERT INTO circus.circus_event_journal
            (source, event_id, event_type, subject, observed_at,
             instance_id, epoch_id, sequence, run_id,
             envelope_json, raw_body, raw_body_sha256, received_at)
        VALUES
            (@source, @eventId, @eventType, @subject, @observedAt,
             @instanceId, @epochId, @sequence, @runId,
             @envelopeJson::jsonb, @rawBody, @rawBodySha256, clock_timestamp())
        ON CONFLICT DO NOTHING
        RETURNING journal_position;
        """

    let selectColumns =
        "journal_position, source, event_id, instance_id, epoch_id, sequence, run_id, event_type, envelope_json, raw_body"

    let selectByIdentity =
        $"""
        SELECT {selectColumns}
        FROM circus.circus_event_journal
        WHERE source = @source AND event_id = @eventId;
        """

    let selectByStreamPosition =
        $"""
        SELECT {selectColumns}
        FROM circus.circus_event_journal
        WHERE instance_id = @instanceId AND epoch_id = @epochId AND sequence = @sequence;
        """

    let selectByPosition =
        $"""
        SELECT {selectColumns}
        FROM circus.circus_event_journal
        WHERE journal_position = @journalPosition;
        """

    let selectByRunId =
        $"""
        SELECT {selectColumns}
        FROM circus.circus_event_journal
        WHERE run_id = @runId ORDER BY journal_position ASC;
        """

    let selectAllOrdered =
        $"""
        SELECT {selectColumns}
        FROM circus.circus_event_journal ORDER BY journal_position ASC;
        """

    let countJournal = "SELECT COUNT(*) FROM circus.circus_event_journal;"

    let existsByIdentity =
        """
        SELECT EXISTS(SELECT 1 FROM circus.circus_event_journal WHERE source = @source AND event_id = @eventId);
        """

    let checkEnvelopeEqual =
        """
        SELECT (envelope_json = @incomingEnvelope::jsonb)
        FROM circus.circus_event_journal
        WHERE source = @source AND event_id = @eventId;
        """

module JournalSqlExec =
    let private mapRow (reader: DbDataReader) : JournalEntry =
        { JournalPosition = JournalPosition(reader.GetInt64(0))
          Source = reader.GetString(1)
          EventId = reader.GetString(2)
          InstanceId = reader.GetString(3)
          EpochId = reader.GetGuid(4)
          Sequence = reader.GetInt64(5)
          RunId = reader.GetGuid(6)
          EventType = reader.GetString(7)
          EnvelopeJson = reader.GetString(8)
          RawBody = reader.GetFieldValue<byte[]>(9) }

    let tryInsert (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (candidate: JournalCandidate) : Task<int64 option> =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- JournalSql.insertJournal

            cmd.Parameters.AddWithValue("source", NpgsqlDbType.Text, EventSource.value candidate.Identity.Source)
            |> ignore

            cmd.Parameters.AddWithValue("eventId", NpgsqlDbType.Text, EventId.value candidate.Identity.EventId)
            |> ignore

            cmd.Parameters.AddWithValue("eventType", NpgsqlDbType.Text, EventType.value candidate.EventType)
            |> ignore

            cmd.Parameters.AddWithValue("subject", NpgsqlDbType.Text, candidate.Subject)
            |> ignore

            cmd.Parameters.AddWithValue("observedAt", NpgsqlDbType.TimestampTz, candidate.ObservedAt)
            |> ignore

            cmd.Parameters.AddWithValue(
                "instanceId",
                NpgsqlDbType.Text,
                InstanceId.value candidate.StreamPosition.InstanceId
            )
            |> ignore

            cmd.Parameters.AddWithValue("epochId", NpgsqlDbType.Uuid, EpochId.value candidate.StreamPosition.EpochId)
            |> ignore

            cmd.Parameters.AddWithValue(
                "sequence",
                NpgsqlDbType.Bigint,
                EventSequence.value candidate.StreamPosition.Sequence
            )
            |> ignore

            cmd.Parameters.AddWithValue("runId", NpgsqlDbType.Uuid, RunId.value candidate.RunId)
            |> ignore

            cmd.Parameters.AddWithValue("envelopeJson", NpgsqlDbType.Jsonb, candidate.EnvelopeJson)
            |> ignore

            cmd.Parameters.AddWithValue("rawBody", NpgsqlDbType.Bytea, candidate.RawBody)
            |> ignore

            cmd.Parameters.AddWithValue("rawBodySha256", NpgsqlDbType.Bytea, SHA256.HashData candidate.RawBody)
            |> ignore

            use! reader = cmd.ExecuteReaderAsync()

            if reader.Read() then
                return Some(reader.GetInt64(0))
            else
                return None
        }

    let private withParameters (cmd: NpgsqlCommand) (source: string) (eventId: string) =
        cmd.Parameters.AddWithValue("source", NpgsqlDbType.Text, source) |> ignore
        cmd.Parameters.AddWithValue("eventId", NpgsqlDbType.Text, eventId) |> ignore

    let lookupByIdentity
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (source: string)
        (eventId: string)
        : Task<JournalEntry option> =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- JournalSql.selectByIdentity
            withParameters cmd source eventId
            use! reader = cmd.ExecuteReaderAsync()

            if reader.Read() then
                return Some(mapRow reader)
            else
                return None
        }

    let lookupByStreamPosition
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (instanceId: string)
        (epochId: Guid)
        (sequence: int64)
        : Task<JournalEntry option> =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- JournalSql.selectByStreamPosition

            cmd.Parameters.AddWithValue("instanceId", NpgsqlDbType.Text, instanceId)
            |> ignore

            cmd.Parameters.AddWithValue("epochId", NpgsqlDbType.Uuid, epochId) |> ignore
            cmd.Parameters.AddWithValue("sequence", NpgsqlDbType.Bigint, sequence) |> ignore
            use! reader = cmd.ExecuteReaderAsync()

            if reader.Read() then
                return Some(mapRow reader)
            else
                return None
        }

    let lookupByPosition
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (position: int64)
        : Task<JournalEntry option> =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- JournalSql.selectByPosition

            cmd.Parameters.AddWithValue("journalPosition", NpgsqlDbType.Bigint, position)
            |> ignore

            use! reader = cmd.ExecuteReaderAsync()

            if reader.Read() then
                return Some(mapRow reader)
            else
                return None
        }

    let lookupByRunId (dataSource: NpgsqlDataSource) (runId: RunId) : Task<JournalEntry list> =
        task {
            use cmd = dataSource.CreateCommand(JournalSql.selectByRunId)

            cmd.Parameters.AddWithValue("runId", NpgsqlDbType.Uuid, RunId.value runId)
            |> ignore

            use! reader = cmd.ExecuteReaderAsync()
            let mutable entries = []

            while reader.Read() do
                entries <- mapRow reader :: entries

            return List.rev entries
        }

    let lookupAllOrdered (dataSource: NpgsqlDataSource) : Task<JournalEntry list> =
        task {
            use cmd = dataSource.CreateCommand(JournalSql.selectAllOrdered)
            use! reader = cmd.ExecuteReaderAsync()
            let mutable entries = []

            while reader.Read() do
                entries <- mapRow reader :: entries

            return List.rev entries
        }

    let count (dataSource: NpgsqlDataSource) : Task<int64> =
        task {
            use cmd = dataSource.CreateCommand(JournalSql.countJournal)
            let! value = cmd.ExecuteScalarAsync()
            return Convert.ToInt64 value
        }

    let checkEnvelopeEqual
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (source: string)
        (eventId: string)
        (incomingEnvelope: string)
        : Task<bool> =
        task {
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- JournalSql.checkEnvelopeEqual

            cmd.Parameters.AddWithValue("incomingEnvelope", NpgsqlDbType.Jsonb, incomingEnvelope)
            |> ignore

            withParameters cmd source eventId
            let! result = cmd.ExecuteScalarAsync()
            return not (isNull result) && Convert.ToBoolean result
        }
