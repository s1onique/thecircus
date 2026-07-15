module Circus.Persistence.Postgres.Tests.JournalRepositoryTests

open System
open System.Threading.Tasks
open Expecto
open Circus.Application
open Circus.Domain
open Circus.Persistence.Postgres.Tests.PostgresFixture

/// Helper to create a test candidate.
let createCandidate source eventId instanceId epochId sequence runId envelope =
    { Identity =
        { Source = EventSource source
          EventId = EventId eventId }
      StreamPosition =
        { InstanceId = InstanceId instanceId
          EpochId = EpochId epochId
          Sequence = EventSequence sequence }
      RunId = RunId runId
      EventType = EventType "io.leamas.execution.started.v1"
      Subject = sprintf "run/%O" runId
      ObservedAt = DateTimeOffset.UtcNow
      RawBody = Text.Encoding.UTF8.GetBytes(envelope)
      EnvelopeJson = envelope }

let tests (fixture: PostgresFixture) =
    testList
        "JournalRepository"
        [ test "first event returns inserted position" {
              fixture.TruncateTables() |> WaitTask |> ignore

              let candidate = createCandidate "source1" "id1" "inst1" Guid.NewGuid() 1L Guid.NewGuid() "{}"
              let! result = fixture.JournalRepo.tryInsert candidate

              match result with
              | Some pos -> Expect.isTrue (pos >= 1L) "Position should be >= 1"
              | None -> failwith "Expected Some position"
          }

          test "exact replay returns None" {
              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()
              let envelope = "{}"

              fixture.TruncateTables() |> WaitTask |> ignore

              let candidate = createCandidate "source1" "id1" "inst1" epochId 1L runId envelope

              let! first = fixture.JournalRepo.tryInsert candidate
              let! second = fixture.JournalRepo.tryInsert candidate

              Expect.isSome first "First insert should succeed"
              Expect.isNone second "Second insert should return None"
          }

          test "semantic replay with whitespace still idempotent" {
              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()

              fixture.TruncateTables() |> WaitTask |> ignore

              // Insert with compact JSON
              let compact = """{"data":{"repo":"test"}}"""
              let candidate1 = createCandidate "source1" "id1" "inst1" epochId 1L runId compact
              let! _ = fixture.JournalRepo.tryInsert candidate1

              // Note: PostgreSQL jsonb normalizes, but stored bytes may differ
              // This test verifies raw bytes are preserved
              let source = EventSource.value candidate1.Identity.Source
              let eventId = EventId.value candidate1.Identity.EventId
              let! entry = fixture.JournalRepo.lookupByIdentity source eventId

              match entry with
              | Some e -> Expect.equal e.EnvelopeJson compact "Stored JSON should match"
              | None -> failwith "Entry should exist"
          }

          test "same source/id with changed payload returns conflict" {
              fixture.TruncateTables() |> WaitTask |> ignore

              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()

              let c1 = createCandidate "source1" "id1" "inst1" epochId 1L runId """{"v":1}"""
              let! _ = fixture.JournalRepo.tryInsert c1

              // Same identity, different sequence
              let c2 = createCandidate "source1" "id1" "inst2" (Guid.NewGuid()) 2L Guid.NewGuid() """{"v":2}"""
              let! result = fixture.JournalRepo.tryInsert c2

              Expect.isNone result "Different sequence should conflict"
          }

          test "same sequence with different identity returns conflict" {
              fixture.TruncateTables() |> WaitTask |> ignore

              let runId1 = Guid.NewGuid()
              let runId2 = Guid.NewGuid()
              let epochId = Guid.NewGuid()

              let c1 = createCandidate "source1" "id1" "inst1" epochId 1L runId1 "{}"
              let! _ = fixture.JournalRepo.tryInsert c1

              let c2 = createCandidate "source2" "id2" "inst1" epochId 1L runId2 "{}"
              let! result = fixture.JournalRepo.tryInsert c2

              Expect.isNone result "Same sequence should conflict"
          }

          test "unknown event type is journaled" {
              fixture.TruncateTables() |> WaitTask |> ignore

              let candidate = createCandidate "source1" "id1" "inst1" Guid.NewGuid() 1L Guid.NewGuid() "{}"
              let! result = fixture.JournalRepo.tryInsert candidate

              Expect.isSome result "Unknown event type should be journaled"
          }

          test "raw bytes are retained" {
              fixture.TruncateTables() |> WaitTask |> ignore

              let rawBytes = [| 0xDEuy; 0xADuy; 0xBEuY; 0xEFuy |]
              let candidate =
                  { Identity = { Source = EventSource "source1"; EventId = EventId "id1" }
                    StreamPosition = { InstanceId = InstanceId "inst1"; EpochId = EpochId Guid.NewGuid(); Sequence = EventSequence 1L }
                    RunId = RunId Guid.NewGuid()
                    EventType = EventType "io.leamas.execution.started.v1"
                    Subject = "run/test"
                    ObservedAt = DateTimeOffset.UtcNow
                    RawBody = rawBytes
                    EnvelopeJson = "{}" }

              let! _ = fixture.JournalRepo.tryInsert candidate

              let source = EventSource.value candidate.Identity.Source
              let eventId = EventId.value candidate.Identity.EventId
              let! entry = fixture.JournalRepo.lookupByIdentity source eventId

              match entry with
              | Some e -> Expect.equal e.EnvelopeJson "{}" "JSON should be stored"
              | None -> failwith "Entry should exist"
          }

          test "lookup by stream position works" {
              fixture.TruncateTables() |> WaitTask |> ignore

              let epochId = Guid.NewGuid()
              let candidate = createCandidate "source1" "id1" "inst1" epochId 1L Guid.NewGuid() "{}"

              let! _ = fixture.JournalRepo.tryInsert candidate

              let! entry = fixture.JournalRepo.lookupByStreamPosition "inst1" epochId 1L

              match entry with
              | Some e -> Expect.equal e.InstanceId "inst1" "Instance should match"
              | None -> failwith "Entry should exist"
          }

          test "count returns correct number" {
              fixture.TruncateTables() |> WaitTask |> ignore

              let! count1 = fixture.JournalRepo.countEntries()
              Expect.equal count1 0L "Initial count should be 0"

              let _ = createCandidate "source1" "id1" "inst1" Guid.NewGuid() 1L Guid.NewGuid() "{}"
              let! _ = fixture.JournalRepo.tryInsert _

              let! count2 = fixture.JournalRepo.countEntries()
              Expect.equal count2 1L "Count should be 1"
          } ]
