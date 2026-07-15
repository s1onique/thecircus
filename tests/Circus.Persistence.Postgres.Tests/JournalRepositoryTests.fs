module Circus.Persistence.Postgres.Tests.JournalRepositoryTests

open System
open System.Linq
open System.Threading.Tasks
open Expecto
open Circus.Application
open Circus.Domain
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.Support

let private ingest (fixture: PostgresFixture) request =
    fixture.Ingestion.Ingest request |> wait

let tests (fixture: PostgresFixture) =
    testList
        "Ingestion service persistence"
        [ test "new event is inserted through the service and projection starts at version one" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let event = startedEvent "service-insert" runId (Guid.NewGuid()) 1L

              match ingest fixture (requestWithFormatting event false) with
              | Success(Inserted _, Some projection) ->
                  Expect.equal projection.Version 1L "First version is one"
                  Expect.equal projection.State StartedOnly "Started projection"
              | result -> failwithf "Expected inserted, got %A" result
          }

          test "semantic JSON replay preserves first raw bytes" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let event = startedEvent "semantic-replay" runId (Guid.NewGuid()) 1L
              let first = requestWithFormatting event false
              let replay = requestWithFormatting event true
              let firstRaw = first.RawBody

              match ingest fixture first, ingest fixture replay with
              | Success(Inserted _, _), Success(IdempotentReplay _, _) ->
                  let entry =
                      fixture.JournalRepo.LookupByIdentity
                          (EventSource.value event.Source)
                          (EventId.value event.EventId)
                      |> wait
                      |> Option.get

                  Expect.equal entry.RawBody firstRaw "Original bytes are immutable"
                  Expect.equal (fixture.JournalRepo.Count() |> wait) 1L "One authority row"
              | firstResult, secondResult -> failwithf "Expected insert/replay, got %A and %A" firstResult secondResult
          }

          test "same identity with semantically different content is a conflict" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()
              let firstEvent = startedEvent "identity-conflict" runId epoch 1L
              let changed = finishedEvent "identity-conflict" runId epoch 2L Failed

              let firstResult = ingest fixture (requestWithFormatting firstEvent false)
              let secondResult = ingest fixture (requestWithFormatting changed false)

              match firstResult, secondResult with
              | Success(Inserted _, _), Success(EventIdentityConflict _, _) ->
                  Expect.equal (fixture.JournalRepo.Count() |> wait) 1L "Conflict is not journal authority"
              | firstValue, secondValue -> failwithf "Expected insert/conflict, got %A and %A" firstValue secondValue
          }

          test "unknown events are journaled but do not create or mutate a projection" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let unknown = unknownEvent "unknown" runId (Guid.NewGuid()) 1L

              match ingest fixture (requestWithFormatting unknown false) with
              | Success(Inserted _, None) ->
                  match fixture.ProjectionRepo.GetByRunId unknown.RunId |> wait with
                  | Ok None -> ()
                  | other -> failwithf "Unknown event created projection: %A" other
              | other -> failwithf "Expected unknown event insertion, got %A" other
          }

          test "journal inspection retains exact bytes and raw digest" {
              fixture.Reset()
              let event = startedEvent "raw-authority" (Guid.NewGuid()) (Guid.NewGuid()) 1L
              let body = "{\n  \"distinctive\": true\n}\n"
              // Use a valid event body with distinctive whitespace so the
              // request still enters the real service path.
              let body = requestWithFormatting event true
              ingest fixture body |> ignore

              let entry =
                  fixture.JournalRepo.LookupByIdentity (EventSource.value event.Source) (EventId.value event.EventId)
                  |> wait
                  |> Option.get

              Expect.equal entry.RawBody body.RawBody "Exact accepted bytes"

              let digest =
                  use sha = System.Security.Cryptography.SHA256.Create()
                  sha.ComputeHash entry.RawBody

              use conn = fixture.AdminDataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()

              cmd.CommandText <-
                  "SELECT raw_body_sha256 FROM circus.circus_event_journal WHERE source = 'urn:test:producer' AND event_id = 'raw-authority'"

              let stored = cmd.ExecuteScalar() :?> byte[]
              Expect.equal stored digest "Digest matches raw bytes"
          }

          test "projection repository decodes the committed service projection" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let event = startedEvent "projection-read" runId (Guid.NewGuid()) 1L
              ingest fixture (requestWithFormatting event false) |> ignore

              match fixture.ProjectionRepo.GetByRunId event.RunId |> wait with
              | Ok(Some projection) ->
                  Expect.equal projection.RunId event.RunId "Run authority"
                  Expect.equal projection.Version 1L "Version authority"

                  Expect.equal
                      projection.Repository
                      (Some(RepositoryRef.tryCreate "circus-repository" |> Option.get))
                      "Repository authority"
              | other -> failwithf "Expected decoded projection, got %A" other
          } ]
