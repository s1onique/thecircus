module Circus.Persistence.Postgres.Tests.RetryCompositionTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open Circus.Application
open Circus.Persistence.Postgres
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.Support

let wait (value: Task<'value>) = value.GetAwaiter().GetResult()
let waitUnit (value: Task) = value.GetAwaiter().GetResult()

let private noDelay (_: int) : Task<unit> = Task.FromResult(())

/// Build an observer that raises the supplied faulty exception on the first
/// `failureBudget` invocations of `TransactionBegun`, then becomes a no-op.
/// Each invocation counts so the test can assert the attempt count.
type CountingObserver(failureBudget: int, sqlState: string) =
    let mutable begun = 0
    let mutable opened = 0
    let mutable mutations = 0

    member _.ConnectionOpenedCount = opened
    member _.TransactionBegunCount = begun
    member _.BeforeContestedMutationCount = mutations

    member _.Observer =
        { ConnectionOpened = fun _ -> opened <- opened + 1
          TransactionBegun =
            fun _ _ ->
                begun <- begun + 1

                if begun <= failureBudget then
                    raise (faultyNpgsqlException sqlState)
          BeforeContestedMutation = fun _ _ -> mutations <- mutations + 1 }

let private freshFixture (fixture: PostgresFixture) = fixture.Reset()

let tests (fixture: PostgresFixture) =
    testList
        "Production retry composition"
        [ test "successful first attempt performs exactly one transaction begun and zero delays" {
              freshFixture fixture
              let observer = CountingObserver(0, "40001")

              let service =
                  IngestEventService.createWithPolicy fixture.DataSource observer.Observer noDelay

              let event = startedEvent "retry-success" (Guid.NewGuid()) (Guid.NewGuid()) 1L

              match service.Ingest(compactRequest event) |> wait with
              | Success _ ->
                  Expect.equal observer.ConnectionOpenedCount 1 "Exactly one connection opened"
                  Expect.equal observer.TransactionBegunCount 1 "Exactly one transaction begun"
                  Expect.equal (fixture.JournalRepo.Count() |> wait) 1L "Exactly one journal row"
              | other -> failwithf "Expected success, got %A" other
          }

          test "two retryable 40001 failures then success performs exactly three attempts and two delays" {
              freshFixture fixture
              let observer = CountingObserver(2, "40001")
              let mutable delays = 0

              let delay _ =
                  delays <- delays + 1
                  Task.FromResult(())

              let service =
                  IngestEventService.createWithPolicy fixture.DataSource observer.Observer delay

              let event = startedEvent "retry-40001" (Guid.NewGuid()) (Guid.NewGuid()) 1L

              match service.Ingest(compactRequest event) |> wait with
              | Success _ ->
                  Expect.equal observer.TransactionBegunCount 3 "Three transactions begun"
                  Expect.equal delays 2 "Two delays between retries"
                  Expect.equal (fixture.JournalRepo.Count() |> wait) 1L "Exactly one journal row after retry"
              | other -> failwithf "Expected success after retries, got %A" other
          }

          test "40P01 deadlock is also classified as retryable" {
              freshFixture fixture
              let observer = CountingObserver(1, "40P01")
              let mutable delays = 0

              let delay _ =
                  delays <- delays + 1
                  Task.FromResult(())

              let service =
                  IngestEventService.createWithPolicy fixture.DataSource observer.Observer delay

              let event = startedEvent "retry-40p01" (Guid.NewGuid()) (Guid.NewGuid()) 1L

              match service.Ingest(compactRequest event) |> wait with
              | Success _ ->
                  Expect.equal observer.TransactionBegunCount 2 "Two transactions begun"
                  Expect.equal delays 1 "One delay between retry"
                  Expect.equal (fixture.JournalRepo.Count() |> wait) 1L "Exactly one journal row after retry"
              | other -> failwithf "Expected success after retry, got %A" other
          }

          test "exhaustion at maximum three performs no fourth attempt and yields SerializationRetriesExhausted" {
              freshFixture fixture
              let observer = CountingObserver(99, "40001")
              let mutable delays = 0

              let delay _ =
                  delays <- delays + 1
                  Task.FromResult(())

              let service =
                  IngestEventService.createWithPolicy fixture.DataSource observer.Observer delay

              let event = startedEvent "retry-exhaustion" (Guid.NewGuid()) (Guid.NewGuid()) 1L

              match service.Ingest(compactRequest event) |> wait with
              | PersistenceFailure SerializationRetriesExhausted ->
                  Expect.equal observer.TransactionBegunCount 3 "No hidden fourth attempt"
                  Expect.equal delays 2 "Exactly two delays"
                  Expect.equal (fixture.JournalRepo.Count() |> wait) 0L "No leaked journal row"
              | other -> failwithf "Expected typed exhaustion, got %A" other
          }

          test "permanent typed failure performs exactly one attempt" {
              freshFixture fixture
              let observer = CountingObserver(99, "40001")
              // Inject a ProjectionInvariantFailed via a corrupt projection so
              // the service reaches its typed AppendFailed path.
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()
              let firstEvent = startedEvent "permanent-establish" runId epoch 1L

              match fixture.Ingestion.Ingest(compactRequest firstEvent) |> wait with
              | Success(Inserted _, _) -> ()
              | other -> failwithf "Expected initial insert, got %A" other

              fixture.ExecuteAsAdmin(
                  $"UPDATE circus.circus_run_projection SET conflict_count = 5 WHERE run_id = '{runId}'"
              )

              // Reset the observer so this test exercises the failure path
              // without the prior observer's bookkeeping.
              let observer2 = CountingObserver(0, "40001")

              let service =
                  IngestEventService.createWithPolicy fixture.DataSource observer2.Observer noDelay

              let secondEvent = startedEvent "permanent-second" runId epoch 2L

              match service.Ingest(compactRequest secondEvent) |> wait with
              | PersistenceFailure ProjectionInvariantFailed ->
                  Expect.equal observer2.TransactionBegunCount 1 "Exactly one attempt"
                  Expect.equal (fixture.JournalRepo.Count() |> wait) 1L "Permanent failure rolled back"
              | other -> failwithf "Expected typed permanent failure, got %A" other
          }

          test "cancellation propagates through exactly one attempt" {
              freshFixture fixture
              let mutable begun = 0

              let observer =
                  { ConnectionOpened = fun _ -> ()
                    TransactionBegun =
                      fun _ _ ->
                          begun <- begun + 1
                          // Raise OperationCanceledException from the real
                          // transaction boundary so the inner catch in the
                          // service re-raises it and the retry policy never
                          // sees a retryable failure.
                          raise (System.OperationCanceledException())
                    BeforeContestedMutation = fun _ _ -> () }

              let service =
                  IngestEventService.createWithPolicy fixture.DataSource observer noDelay

              let event = startedEvent "retry-cancel" (Guid.NewGuid()) (Guid.NewGuid()) 1L

              let mutable caught = false

              try
                  let result = service.Ingest(compactRequest event)
                  let _ = wait result
                  failwith "Expected OperationCanceledException to propagate"
              with
              | :? System.OperationCanceledException -> caught <- true
              | _ -> ()

              Expect.isTrue caught "OperationCanceledException propagates out of the service"
              Expect.equal begun 1 "Exactly one attempt reached the transaction boundary"
              Expect.equal (fixture.JournalRepo.Count() |> wait) 0L "Cancelled attempt rolled back"
          }

          test "every retry uses a distinct NpgsqlConnection and NpgsqlTransaction" {
              // Correctness evidence: a sequential retry must open a fresh
              // connection and a fresh transaction each attempt.  The
              // backend PID is recorded only as an opaque integer; Npgsql
              // pools physical PostgreSQL connections and a sequential
              // retry may legitimately reuse the same backend PID.  The
              // asserted identities are the NpgsqlConnection and
              // NpgsqlTransaction references held by the observer while
              // the backend is still active.
              freshFixture fixture
              let lockObj = obj ()
              let mutable attemptNumber = 0
              let connections = System.Collections.Generic.List<NpgsqlConnection>()
              let transactions = System.Collections.Generic.List<NpgsqlTransaction>()
              let backendPids = System.Collections.Generic.List<int>()

              let observer =
                  { ConnectionOpened =
                      fun conn ->
                          let pid = conn.ProcessID

                          lock lockObj (fun () ->
                              connections.Add(conn)
                              backendPids.Add(pid))
                    TransactionBegun =
                      fun _ tx ->
                          let current = Interlocked.Increment(&attemptNumber)
                          lock lockObj (fun () -> transactions.Add(tx))

                          if current <= 2 then
                              raise (faultyNpgsqlException "40001")
                    BeforeContestedMutation = fun _ _ -> () }

              let service =
                  IngestEventService.createWithPolicy fixture.DataSource observer noDelay

              let event = startedEvent "retry-distinct" (Guid.NewGuid()) (Guid.NewGuid()) 1L

              match service.Ingest(compactRequest event) |> wait with
              | Success _ ->
                  Expect.equal attemptNumber 3 "Three attempts reach the transaction boundary"
                  Expect.equal connections.Count 3 "Three NpgsqlConnection references captured"
                  Expect.equal transactions.Count 3 "Three NpgsqlTransaction references captured"
                  Expect.equal backendPids.Count 3 "Three backend PID integers recorded"

                  // Identity assertions: the wrapper references recorded by
                  // the observer must be distinct objects, and so must the
                  // transaction references.  Backend PID cardinality is
                  // captured for diagnostic context only and is NOT
                  // asserted to be three distinct values.
                  let distinctConnections =
                      connections
                      |> Seq.distinctBy (fun (c: NpgsqlConnection) -> c :> obj)
                      |> Seq.length

                  let distinctTransactions =
                      transactions
                      |> Seq.distinctBy (fun (t: NpgsqlTransaction) -> t :> obj)
                      |> Seq.length

                  Expect.equal distinctConnections 3 "Three distinct NpgsqlConnection references"
                  Expect.equal distinctTransactions 3 "Three distinct NpgsqlTransaction references"
              | other -> failwithf "Expected success after retries, got %A" other
          } ]
