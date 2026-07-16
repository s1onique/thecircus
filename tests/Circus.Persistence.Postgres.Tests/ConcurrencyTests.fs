module Circus.Persistence.Postgres.Tests.ConcurrencyTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open Circus.Application
open Circus.Contracts
open Circus.Domain
open Circus.Persistence.Postgres
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.Support

let private noDelay (_: int) : Task<unit> = Task.FromResult(())

let private waitAll (tasks: Task<IngestEventResult> list) =
    Task.WhenAll(tasks) |> fun t -> t.GetAwaiter().GetResult()

/// Run the supplied requests through a fresh ingestion service composed
/// with the supplied observer.  The observer's `TransactionBegun` hook
/// is the real database attempt boundary: the test blocks every attempt
/// there until the configured snapshot count has arrived, then records
/// the snapshot and releases the gate.  Any retries that arrive after
/// the gate releases are recorded separately so the test does not assert
/// a precise cardinal of attempts beyond the snapshot.
let private runOverlappingWithSnapshot
    (fixture: PostgresFixture)
    (requests: IngestEventRequest list)
    (snapshotCount: int)
    =
    let entered =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let gate = new ManualResetEventSlim(false)
    let snapshotConnections = new List<NpgsqlConnection>()
    let snapshotTransactions = new List<NpgsqlTransaction>()
    let snapshotPids = new List<int>()
    let postSnapshotTransactions = new List<NpgsqlTransaction>()
    let postSnapshotPids = new List<int>()
    let lockObj = obj ()
    let mutable atSnapshotCount = 0

    let observer =
        { ConnectionOpened = fun _ -> ()
          TransactionBegun =
            fun _ tx ->
                let pid = tx.Connection.ProcessID
                let current = Interlocked.Increment(&atSnapshotCount)

                lock lockObj (fun () ->
                    if current <= snapshotCount then
                        snapshotConnections.Add(tx.Connection)
                        snapshotTransactions.Add(tx)
                        snapshotPids.Add(pid)
                    else
                        postSnapshotTransactions.Add(tx)
                        postSnapshotPids.Add(pid))

                if current = snapshotCount then
                    entered.TrySetResult(()) |> ignore

                if not (gate.Wait(15000)) then
                    failwith "TransactionBegun gate wait timed out"
          BeforeContestedMutation = fun _ _ -> () }

    let service =
        IngestEventService.createWithPolicy fixture.DataSource observer noDelay

    let tasks =
        requests
        |> List.map (fun request -> Task.Run(fun () -> service.Ingest request |> (fun t -> t.GetAwaiter().GetResult())))

    let enteredOk = entered.Task.Wait(15000)

    Expect.isTrue enteredOk "All snapshot attempts reached the database attempt boundary"
    gate.Set()
    let results = waitAll tasks
    gate.Dispose()
    results, snapshotConnections, snapshotTransactions, snapshotPids, postSnapshotTransactions, postSnapshotPids

let tests (fixture: PostgresFixture) =
    testList
        "Concurrent service ingestion"
        [ test
              "twenty overlapping identical events reach the database attempt boundary and yield one insert and nineteen replays" {
              fixture.Reset()
              let event = startedEvent "twenty-identical" (Guid.NewGuid()) (Guid.NewGuid()) 1L
              let request = compactRequest event

              let (results,
                   snapshotConnections,
                   snapshotTransactions,
                   snapshotPids,
                   postSnapshotTransactions,
                   postSnapshotPids) =
                  runOverlappingWithSnapshot fixture (List.replicate 20 request) 20

              // Snapshot identity assertions: every attempt in the first
              // wave opens its own NpgsqlConnection and its own
              // NpgsqlTransaction.  Backend PID cardinality is recorded
              // for diagnostic context only; Npgsql pools physical
              // PostgreSQL connections and a sequential retry may reuse
              // the same backend PID.
              Expect.equal snapshotConnections.Count 20 "Twenty NpgsqlConnection references in the snapshot"
              Expect.equal snapshotTransactions.Count 20 "Twenty NpgsqlTransaction references in the snapshot"
              Expect.equal snapshotPids.Count 20 "Twenty backend PIDs recorded in the snapshot"

              let distinctSnapshotConnections =
                  snapshotConnections
                  |> Seq.distinctBy (fun (c: NpgsqlConnection) -> c :> obj)
                  |> Seq.length

              let distinctSnapshotTransactions =
                  snapshotTransactions
                  |> Seq.distinctBy (fun (t: NpgsqlTransaction) -> t :> obj)
                  |> Seq.length

              Expect.equal distinctSnapshotConnections 20 "Twenty distinct NpgsqlConnection references"
              Expect.equal distinctSnapshotTransactions 20 "Twenty distinct NpgsqlTransaction references"

              let inserted =
                  results
                  |> Array.filter (function
                      | Success(Inserted _, _) -> true
                      | _ -> false)
                  |> Array.length

              let replayed =
                  results
                  |> Array.filter (function
                      | Success(IdempotentReplay _, _) -> true
                      | _ -> false)
                  |> Array.length

              Expect.equal inserted 1 "Exactly one committed insert"
              Expect.equal replayed 19 "All other calls are semantic replays"
              Expect.equal (fixture.JournalRepo.Count() |> wait) 1L "One journal authority"

              match fixture.ProjectionRepo.GetByRunId event.RunId |> wait with
              | Ok(Some projection) -> Expect.equal projection.Version 1L "Replay does not increment version"
              | other -> failwithf "Expected one projection, got %A" other
          }

          test
              "two independent overlapping sequence authorities observe two connection identities, two transactions, one insert and one typed conflict" {
              fixture.Reset()
              let epoch = Guid.NewGuid()
              let first = startedEvent "sequence-winner-a" (Guid.NewGuid()) epoch 7L
              let second = startedEvent "sequence-winner-b" (Guid.NewGuid()) epoch 7L

              let results, snapshotConnections, snapshotTransactions, backendPids, _, _ =
                  runOverlappingWithSnapshot fixture [ compactRequest first; compactRequest second ] 2

              Expect.equal snapshotConnections.Count 2 "Two connection identities"
              Expect.equal snapshotTransactions.Count 2 "Two transactions"

              let distinctSnapshotConnections =
                  snapshotConnections
                  |> Seq.distinctBy (fun (c: NpgsqlConnection) -> c :> obj)
                  |> Seq.length

              let distinctSnapshotTransactions =
                  snapshotTransactions
                  |> Seq.distinctBy (fun (t: NpgsqlTransaction) -> t :> obj)
                  |> Seq.length

              Expect.equal distinctSnapshotConnections 2 "Two distinct NpgsqlConnection references"
              Expect.equal distinctSnapshotTransactions 2 "Two distinct NpgsqlTransaction references"
              Expect.equal backendPids.Count 2 "Two backend PIDs recorded"

              let inserts =
                  results
                  |> Array.filter (function
                      | Success(Inserted _, _) -> true
                      | _ -> false)
                  |> Array.length

              let conflicts =
                  results
                  |> Array.filter (function
                      | Success(SequenceConflict _, _) -> true
                      | _ -> false)
                  |> Array.length

              Expect.equal inserts 1 "One sequence authority commits"
              Expect.equal conflicts 1 "The loser is a sequence conflict"
              Expect.equal (fixture.JournalRepo.Count() |> wait) 1L "Only the winner is durable"
          }

          test "started and finished overlap and converge through the same service reducer" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()
              let started = startedEvent "overlap-start" runId epoch 1L

              let finished =
                  finishedEvent "overlap-finish" runId epoch 2L ExecutionOutcome.Succeeded

              let results, _, _, _, _, _ =
                  runOverlappingWithSnapshot fixture [ compactRequest started; compactRequest finished ] 2

              Expect.equal
                  (results
                   |> Array.filter (function
                       | Success(Inserted _, _) -> true
                       | _ -> false)
                   |> Array.length)
                  2
                  "Both distinct sequence events commit"

              match fixture.ProjectionRepo.GetByRunId started.RunId |> wait with
              | Ok(Some projection) ->
                  Expect.equal projection.State Completed "Completed after overlap"
                  Expect.equal projection.Version 2L "Two authoritative mutations"
              | other -> failwithf "Expected completed projection, got %A" other
          }

          test "a projection failure rolls back journal and projection atomically" {
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
                  let event = startedEvent "rollback" (Guid.NewGuid()) (Guid.NewGuid()) 1L

                  match fixture.Ingestion.Ingest(compactRequest event) |> wait with
                  | PersistenceFailure _ -> ()
                  | other -> failwithf "Expected persistence failure, got %A" other

                  Expect.equal (fixture.JournalRepo.Count() |> wait) 0L "Journal insert rolled back"

                  match fixture.ProjectionRepo.GetByRunId event.RunId |> wait with
                  | Ok None -> ()
                  | other -> failwithf "Projection mutation leaked: %A" other
              finally
                  fixture.ExecuteAsAdmin(
                      "DROP TRIGGER IF EXISTS circus_test_fail_projection ON circus.circus_run_projection; DROP FUNCTION IF EXISTS circus.test_fail_projection();"
                  )

              let retryEvent = startedEvent "rollback-retry" (Guid.NewGuid()) (Guid.NewGuid()) 1L

              match fixture.Ingestion.Ingest(compactRequest retryEvent) |> wait with
              | Success(Inserted _, Some _) -> ()
              | other -> failwithf "Clean retry did not succeed: %A" other
          } ]
