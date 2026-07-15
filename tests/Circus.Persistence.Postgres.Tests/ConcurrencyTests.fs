module Circus.Persistence.Postgres.Tests.ConcurrencyTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Circus.Application
open Circus.Domain
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.Support

let private waitAll (tasks: Task<IngestEventResult> list) = Task.WhenAll(tasks) |> wait

let private runOverlapping (fixture: PostgresFixture) (requests: IngestEventRequest list) =
    let gate = new ManualResetEventSlim(false)

    let entered =
        new TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable count = 0

    let tasks =
        requests
        |> List.map (fun request ->
            Task.Run(fun () ->
                let current = Interlocked.Increment(&count)

                if current = List.length requests then
                    entered.TrySetResult(()) |> ignore

                gate.Wait()
                fixture.Ingestion.Ingest request |> wait))

    Expect.isTrue (entered.Task.Wait(5000)) "All operations reached the overlap barrier"
    gate.Set()
    let result = waitAll tasks
    gate.Dispose()
    result

let tests (fixture: PostgresFixture) =
    testList
        "Concurrent service ingestion"
        [ test "twenty overlapping identical events yield one insert and nineteen replays" {
              fixture.Reset()
              let event = startedEvent "twenty-identical" (Guid.NewGuid()) (Guid.NewGuid()) 1L
              let request = requestWithFormatting event false
              let results = runOverlapping fixture (List.replicate 20 request)

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

          test "two independent overlapping sequence authorities produce one insert and one typed conflict" {
              fixture.Reset()
              let epoch = Guid.NewGuid()
              let first = startedEvent "sequence-winner-a" (Guid.NewGuid()) epoch 7L
              let second = startedEvent "sequence-winner-b" (Guid.NewGuid()) epoch 7L

              let results =
                  runOverlapping fixture [ requestWithFormatting first false; requestWithFormatting second false ]

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
              let finished = finishedEvent "overlap-finish" runId epoch 2L Succeeded

              let results =
                  runOverlapping fixture [ requestWithFormatting started false; requestWithFormatting finished false ]

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

                  match fixture.Ingestion.Ingest(requestWithFormatting event false) |> wait with
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

              match fixture.Ingestion.Ingest(requestWithFormatting retryEvent false) |> wait with
              | Success(Inserted _, Some _) -> ()
              | other -> failwithf "Clean retry did not succeed: %A" other
          } ]
