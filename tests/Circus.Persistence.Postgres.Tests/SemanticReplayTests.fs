module Circus.Persistence.Postgres.Tests.SemanticReplayTests

open System
open System.Security.Cryptography
open System.Threading.Tasks
open Expecto
open Npgsql
open Circus.Application
open Circus.Contracts
open Circus.Domain
open Circus.Persistence.Postgres
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.Support

let wait (value: Task<'value>) = value.GetAwaiter().GetResult()
let waitUnit (value: Task) = value.GetAwaiter().GetResult()

let private ingest (fixture: PostgresFixture) request =
    fixture.Ingestion.Ingest request |> wait

let private journalEntry (fixture: PostgresFixture) (source: EventSource) (eventId: EventId) =
    fixture.JournalRepo.LookupByIdentity (EventSource.value source) (EventId.value eventId)
    |> wait
    |> Option.get

let private digestOf (bytes: byte[]) =
    use sha = SHA256.Create()
    sha.ComputeHash bytes

let private storedDigest (fixture: PostgresFixture) (source: string) (eventId: string) =
    use conn = fixture.AdminDataSource.CreateConnection()
    conn.Open()
    use cmd = conn.CreateCommand()

    cmd.CommandText <-
        $"SELECT raw_body_sha256 FROM circus.circus_event_journal WHERE source = '{source}' AND event_id = '{eventId}'"

    cmd.ExecuteScalar() :?> byte[]

let private assertReplay
    (fixture: PostgresFixture)
    (label: string)
    (event: ValidatedEvent)
    (first: IngestEventRequest)
    (replay: IngestEventRequest)
    =
    let source = event.Source
    let eventId = event.EventId
    let sourceWire = EventSource.value source
    let eventIdWire = EventId.value eventId

    match ingest fixture first, ingest fixture replay with
    | Success(Inserted _, _), Success(IdempotentReplay _, _) ->
        Expect.equal (fixture.JournalRepo.Count() |> wait) 1L $"{label}: one journal row"

        let entry = journalEntry fixture source eventId
        Expect.equal entry.RawBody first.RawBody $"{label}: original raw bytes are immutable"

        match fixture.ProjectionRepo.GetByRunId event.RunId |> wait with
        | Ok(Some projection) -> Expect.equal projection.Version 1L $"{label}: replay does not increment version"
        | other -> failwithf "Expected projection, got %A" other

        let stored = storedDigest fixture sourceWire eventIdWire
        let computed = digestOf first.RawBody
        Expect.equal stored computed $"{label}: stored digest matches the first raw bytes"
    | firstResult, secondResult -> failwithf "%s: expected insert/replay, got %A and %A" label firstResult secondResult

let tests (fixture: PostgresFixture) =
    testList
        "Semantic JSON replay across key reordering"
        [ test "compact original followed by whitespace variant is idempotent replay" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()
              let event = startedEvent "replay-whitespace" runId epoch 1L
              assertReplay fixture "whitespace" event (compactRequest event) (prettyRequest event)
          }

          test "compact original followed by reordered top-level keys is idempotent replay" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()
              let event = startedEvent "replay-top-keys" runId epoch 1L
              assertReplay fixture "top-level keys" event (compactRequest event) (reorderedTopRequest event)
          }

          test "compact original followed by reordered nested data keys is idempotent replay" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()
              let event = startedEvent "replay-data-keys" runId epoch 1L
              assertReplay fixture "nested data keys" event (compactRequest event) (reorderedDataRequest event)
          }

          test "compact original followed by reordered nested checks keys for finished event is idempotent replay" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()

              let event =
                  finishedEventWithChecks "replay-checks-keys" runId epoch 1L ExecutionOutcome.Succeeded 3 1 0

              assertReplay fixture "nested checks keys" event (compactRequest event) (reorderedChecksRequest event)
          }

          test "compact original followed by semantically different content is EventIdentityConflict" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()

              let firstEvent = startedEvent "replay-conflict" runId epoch 1L

              let mutated =
                  finishedEventWithChecks "replay-conflict" runId epoch 1L ExecutionOutcome.Succeeded 3 1 0

              let first = compactRequest firstEvent
              let second = compactRequest mutated

              match ingest fixture first, ingest fixture second with
              | Success(Inserted _, _), Success(EventIdentityConflict _, _) ->
                  Expect.equal (fixture.JournalRepo.Count() |> wait) 1L "Identity conflict is not journal authority"
              | firstResult, secondResult ->
                  failwithf "Expected insert/conflict, got %A and %A" firstResult secondResult
          } ]
