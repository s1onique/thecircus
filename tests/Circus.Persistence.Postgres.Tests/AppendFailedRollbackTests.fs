module Circus.Persistence.Postgres.Tests.AppendFailedRollbackTests

open System
open System.Threading.Tasks
open Expecto
open Npgsql
open Circus.Application
open Circus.Contracts
open Circus.Domain
open Circus.Persistence.Postgres
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.Support

let waitUnit (value: Task) = value.GetAwaiter().GetResult()
let wait (value: Task<'value>) = value.GetAwaiter().GetResult()

let private ingest (fixture: PostgresFixture) request =
    fixture.Ingestion.Ingest request |> wait

let private corruptProjection (fixture: PostgresFixture) (runId: Guid) =
    fixture.ExecuteAsAdmin(
        $"""
        UPDATE circus.circus_run_projection
           SET conflict_count = 5
         WHERE run_id = '{runId}'
        """
    )

let private readProjection (fixture: PostgresFixture) (runId: Guid) =
    use conn = fixture.AdminDataSource.OpenConnection()
    use cmd = conn.CreateCommand()

    cmd.CommandText <-
        $"SELECT state, conflict_count, version, started_journal_position
          FROM circus.circus_run_projection WHERE run_id = '{runId}'"

    use reader = cmd.ExecuteReader()

    if not (reader.Read()) then
        None
    else
        Some
            {| State = reader.GetString 0
               ConflictCount = reader.GetInt32 1
               Version = reader.GetInt64 2
               StartedPosition = reader.GetInt64 3 |}

let private readJournal (fixture: PostgresFixture) (eventId: string) =
    use conn = fixture.AdminDataSource.OpenConnection()
    use cmd = conn.CreateCommand()

    cmd.CommandText <-
        $"SELECT journal_position, source, event_id, raw_body
          FROM circus.circus_event_journal WHERE event_id = '{eventId}'"

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        Some
            {| Position = reader.GetInt64 0
               Source = reader.GetString 1
               EventId = reader.GetString 2
               RawBody = reader.GetFieldValue<byte[]> 3 |}
    else
        None

let private repairProjectionConflictCount (fixture: PostgresFixture) (runId: Guid) =
    fixture.ExecuteAsAdmin(
        $"""
        UPDATE circus.circus_run_projection
           SET conflict_count = 0
         WHERE run_id = '{runId}'
        """
    )

let tests (fixture: PostgresFixture) =
    testList
        "Typed AppendFailed rollback path"
        [ test
              "typed ProjectionInvariantFailed rolls back the new journal row; repairing only the corrupted field lets the same secondEvent succeed" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()

              // 1. Establish the run via a clean insertion.  Capture the
              //    actual journal position returned by the first insert so
              //    we never assume it is 1.
              let firstEvent = startedEvent "rollback-establish" runId epoch 1L
              let firstRequest = compactRequest firstEvent

              let firstPosition =
                  match ingest fixture firstRequest with
                  | Success(Inserted pos, Some _) -> pos
                  | other -> failwithf "Expected initial insert, got %A" other

              // 2. Corrupt only conflict_count on the existing projection.
              //    All other fields (state, version, started authority)
              //    remain valid for the first event.
              corruptProjection fixture runId

              // 3. Attempt a second insertion for the same run.  The update
              //    step must load the corrupt projection and surface the
              //    typed AppendFailed path.  The second event has a
              //    different identity but the same run and a fresh sequence.
              let secondEvent = startedEvent "rollback-second" runId epoch 2L
              let secondRequest = compactRequest secondEvent

              match ingest fixture secondRequest with
              | PersistenceFailure ProjectionInvariantFailed -> ()
              | other -> failwithf "Expected typed ProjectionInvariantFailed, got %A" other

              // 4. The new journal event must not be present.
              Expect.equal (fixture.JournalRepo.Count() |> wait) 1L "New journal row was rolled back"
              Expect.isTrue (readJournal fixture "rollback-second" |> Option.isNone) "Second event_id not persisted"

              // 5. The corrupt projection's other fields must be unchanged.
              let snapshotAfterRollback = readProjection fixture runId
              Expect.isTrue snapshotAfterRollback.IsSome "Projection still exists"
              Expect.equal snapshotAfterRollback.Value.State "StartedOnly" "State is unchanged"
              Expect.equal snapshotAfterRollback.Value.Version 1L "Version is unchanged"

              Expect.equal
                  snapshotAfterRollback.Value.StartedPosition
                  (JournalPosition.value firstPosition)
                  "Started authority preserved"

              Expect.equal snapshotAfterRollback.Value.ConflictCount 5 "Conflict count still 5"

              // 6. Repair the projection by restoring conflict_count to 0
              //    so it represents the valid first-event state.  No other
              //    field is touched.
              repairProjectionConflictCount fixture runId

              let snapshotAfterRepair = readProjection fixture runId
              Expect.isTrue snapshotAfterRepair.IsSome "Projection still exists after repair"
              Expect.equal snapshotAfterRepair.Value.ConflictCount 0 "Conflict count restored to 0"
              Expect.equal snapshotAfterRepair.Value.State "StartedOnly" "State still StartedOnly"
              Expect.equal snapshotAfterRepair.Value.Version 1L "Version still 1"

              Expect.equal
                  snapshotAfterRepair.Value.StartedPosition
                  (JournalPosition.value firstPosition)
                  "Started authority still first position"

              // 7. Retry the exact same secondEvent.  The reducer sees an
              //    existing StartedOnly projection and a duplicate started
              //    event, so it marks the projection as Conflicted.
              match ingest fixture secondRequest with
              | Success(Inserted _secondPos, Some projection) ->
                  Expect.equal projection.State Conflicted "Duplicate started marks conflict"
                  Expect.equal projection.Version 2L "Version incremented by one"
                  Expect.equal projection.ConflictCount 1 "One conflict recorded"
                  Expect.equal projection.StartedEvent (Some firstPosition) "Original started authority preserved"
                  Expect.equal projection.FinishedEvent None "Finished authority still null"
              | other -> failwithf "Expected recovered insert, got %A" other

              // 8. The journal now contains first and second with their
              //    original raw bytes preserved byte-for-byte.
              Expect.equal (fixture.JournalRepo.Count() |> wait) 2L "Both events persisted after repair"
              let firstJournalAfter = readJournal fixture "rollback-establish"
              let secondJournalAfter = readJournal fixture "rollback-second"
              Expect.isTrue firstJournalAfter.IsSome "First journal row preserved after retry"
              Expect.isTrue secondJournalAfter.IsSome "Second journal row preserved after retry"

              Expect.notEqual
                  (Option.get firstJournalAfter).Position
                  (Option.get secondJournalAfter).Position
                  "First and second positions differ"

              // 9. Original raw bytes are preserved byte-for-byte on both
              //    the first and the retried second journal authorities.
              Expect.sequenceEqual
                  (Option.get firstJournalAfter).RawBody
                  firstRequest.RawBody
                  "First authority retains its exact raw bytes"

              Expect.sequenceEqual
                  (Option.get secondJournalAfter).RawBody
                  secondRequest.RawBody
                  "Retried second authority retains its exact raw bytes"
          }

          test "trigger-thrown atomicity remains adjacent evidence" {
              fixture.Reset()

              fixture.ExecuteAsAdmin(
                  """
                  CREATE OR REPLACE FUNCTION circus.test_fail_projection()
                  RETURNS trigger LANGUAGE plpgsql
                  SET search_path = pg_catalog, circus
                  AS $$ BEGIN RAISE EXCEPTION 'test projection failure'; END; $$;
                  ALTER FUNCTION circus.test_fail_projection() OWNER TO circus_owner;
                  GRANT EXECUTE ON FUNCTION circus.test_fail_projection() TO circus_app;
                  DROP TRIGGER IF EXISTS circus_test_fail_projection ON circus.circus_run_projection;
                  CREATE TRIGGER circus_test_fail_projection
                    BEFORE INSERT ON circus.circus_run_projection
                    FOR EACH ROW EXECUTE FUNCTION circus.test_fail_projection();
                  """
              )

              try
                  let event = startedEvent "trigger-rollback" (Guid.NewGuid()) (Guid.NewGuid()) 1L

                  match ingest fixture (compactRequest event) with
                  | PersistenceFailure _ -> ()
                  | other -> failwithf "Expected persistence failure, got %A" other

                  Expect.equal (fixture.JournalRepo.Count() |> wait) 0L "Trigger exception rolls back journal"

                  match fixture.ProjectionRepo.GetByRunId event.RunId |> wait with
                  | Ok None -> ()
                  | other -> failwithf "Projection mutation leaked: %A" other
              finally
                  fixture.ExecuteAsAdmin(
                      "DROP TRIGGER IF EXISTS circus_test_fail_projection ON circus.circus_run_projection; DROP FUNCTION IF EXISTS circus.test_fail_projection();"
                  )
          } ]
