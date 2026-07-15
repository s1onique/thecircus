module Circus.Persistence.Postgres.Tests.ProjectionIntegrationTests

open System
open System.Threading.Tasks
open Expecto
open Circus.Application
open Circus.Contracts
open Circus.Domain
open Circus.Persistence.Postgres.Tests.PostgresFixture

let tests (fixture: PostgresFixture) =
    testList
        "Projection"
        [ testTask "started only projection" {
              fixture.TruncateTables() |> WaitTask |> ignore

              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()

              // Create started event
              let candidate =
                  { Identity = { Source = EventSource "source1"; EventId = EventId "id1" }
                    StreamPosition = { InstanceId = InstanceId "inst1"; EpochId = EpochId epochId; Sequence = EventSequence 1L }
                    RunId = RunId runId
                    EventType = EventType "io.leamas.execution.started.v1"
                    Subject = sprintf "run/%O" runId
                    ObservedAt = DateTimeOffset.UtcNow
                    RawBody = [||]
                    EnvelopeJson = "{}" }

              let! inserted = fixture.JournalRepo.tryInsert candidate
              Expect.isSome inserted "Event should be inserted"

              // Build projection from journal
              let journalEntries = fixture.JournalRepo.lookupByRunId runId |> WaitTask

              let events =
                  journalEntries
                  |> List.map (fun e ->
                      let validated: ValidatedEvent =
                          { EventId = EventId e.EventId
                            Source = EventSource e.Source
                            EventType = EventType e.EventType
                            Subject = sprintf "run/%O" e.RunId
                            ObservedAt = DateTimeOffset.UtcNow
                            InstanceId = InstanceId e.InstanceId
                            EpochId = EpochId e.EpochId
                            Sequence = EventSequence e.Sequence
                            RunId = RunId e.RunId
                            Extensions = Map.empty
                            Event =
                                ExecutionStartedEvent
                                    { RunId = RunId e.RunId
                                      Repository = RepositoryRef "test"
                                      ActId = None
                                      LeamasVersion = LeamasVersion "1.0.0"
                                      GitRevision = None
                                      StartedBy = None } }

                      JournalPosition e.JournalPosition, validated)

              let projections = RunProjection.rebuild events
              let proj = projections |> Map.find (RunId runId)

              Expect.equal proj.State StartedOnly "State should be StartedOnly"
          }

          testTask "unknown event leaves projection unchanged" {
              fixture.TruncateTables() |> WaitTask |> ignore

              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()

              // Create started event
              let startedCandidate =
                  { Identity = { Source = EventSource "source1"; EventId = EventId "id1" }
                    StreamPosition = { InstanceId = InstanceId "inst1"; EpochId = EpochId epochId; Sequence = EventSequence 1L }
                    RunId = RunId runId
                    EventType = EventType "io.leamas.execution.started.v1"
                    Subject = sprintf "run/%O" runId
                    ObservedAt = DateTimeOffset.UtcNow
                    RawBody = [||]
                    EnvelopeJson = "{}" }

              let! _ = fixture.JournalRepo.tryInsert startedCandidate

              // Create unknown event
              let unknownCandidate =
                  { Identity = { Source = EventSource "source1"; EventId = EventId "id2" }
                    StreamPosition = { InstanceId = InstanceId "inst1"; EpochId = EpochId epochId; Sequence = EventSequence 2L }
                    RunId = RunId runId
                    EventType = EventType "io.leamas.execution.unknown.v1"
                    Subject = sprintf "run/%O" runId
                    ObservedAt = DateTimeOffset.UtcNow
                    RawBody = [||]
                    EnvelopeJson = "{}" }

              let! _ = fixture.JournalRepo.tryInsert unknownCandidate

              // Build projection - only started event should affect it
              let journalEntries = fixture.JournalRepo.lookupByRunId runId |> WaitTask
              Expect.equal (List.length journalEntries) 2 "Both events should be in journal"

              let events =
                  journalEntries
                  |> List.map (fun e ->
                      let validated: ValidatedEvent =
                          { EventId = EventId e.EventId
                            Source = EventSource e.Source
                            EventType = EventType e.EventType
                            Subject = sprintf "run/%O" e.RunId
                            ObservedAt = DateTimeOffset.UtcNow
                            InstanceId = InstanceId e.InstanceId
                            EpochId = EpochId e.EpochId
                            Sequence = EventSequence e.Sequence
                            RunId = RunId e.RunId
                            Extensions = Map.empty
                            Event = UnrecognizedEvent { EventType = e.EventType; Data = None } }

                      JournalPosition e.JournalPosition, validated)

              let projections = RunProjection.rebuild events
              let proj = projections |> Map.find (RunId runId)

              // Unknown event should not have created a conflict
              Expect.equal proj.State StartedOnly "State should still be StartedOnly"
              Expect.equal proj.ConflictCount 0 "No conflicts"
          }

          testTask "rebuilt and incremental projections are equal" {
              fixture.TruncateTables() |> WaitTask |> ignore

              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()

              // Insert started and finished events
              for seq in [ 1L; 2L ] do
                  let candidate =
                      { Identity = { Source = EventSource "source1"; EventId = EventId (sprintf "id%d" seq) }
                        StreamPosition = { InstanceId = InstanceId "inst1"; EpochId = EpochId epochId; Sequence = EventSequence seq }
                        RunId = RunId runId
                        EventType = EventType (if seq = 1L then "io.leamas.execution.started.v1" else "io.leamas.execution.finished.v1")
                        Subject = sprintf "run/%O" runId
                        ObservedAt = DateTimeOffset.UtcNow
                        RawBody = [||]
                        EnvelopeJson = "{}" }

                  let! _ = fixture.JournalRepo.tryInsert candidate
                  ()

              // Build projection incrementally
              let journalEntries = fixture.JournalRepo.lookupByRunId runId |> WaitTask

              let mutable incrementalProj: RunProjection option = None

              for entry in journalEntries do
                  let eventType, evt =
                      if entry.Sequence = 1L then
                          "io.leamas.execution.started.v1",
                          (ExecutionStartedEvent
                              { RunId = RunId entry.RunId
                                Repository = RepositoryRef "test"
                                ActId = None
                                LeamasVersion = LeamasVersion "1.0.0"
                                GitRevision = None
                                StartedBy = None } |> box)
                      else
                          "io.leamas.execution.finished.v1",
                          (ExecutionFinishedEvent
                              { RunId = RunId entry.RunId
                                Outcome = Succeeded
                                DurationMilliseconds = 1000L
                                Summary = None
                                Checks = { Passed = 10; Failed = 0; Skipped = 0 } } |> box)

                  let validated: ValidatedEvent =
                      { EventId = EventId entry.EventId
                        Source = EventSource entry.Source
                        EventType = EventType eventType
                        Subject = sprintf "run/%O" entry.RunId
                        ObservedAt = DateTimeOffset.UtcNow
                        InstanceId = InstanceId entry.InstanceId
                        EpochId = EpochId entry.EpochId
                        Sequence = EventSequence entry.Sequence
                        RunId = RunId entry.RunId
                        Extensions = Map.empty
                        Event = unbox evt }

                  incrementalProj <- RunProjection.applyEvent incrementalProj (JournalPosition entry.JournalPosition) validated

              // Rebuild projection
              let events =
                  journalEntries
                  |> List.map (fun e ->
                      let eventType, evt =
                          if e.Sequence = 1L then
                              "io.leamas.execution.started.v1",
                              (ExecutionStartedEvent
                                  { RunId = RunId e.RunId
                                    Repository = RepositoryRef "test"
                                    ActId = None
                                    LeamasVersion = LeamasVersion "1.0.0"
                                    GitRevision = None
                                    StartedBy = None } |> box)
                          else
                              "io.leamas.execution.finished.v1",
                              (ExecutionFinishedEvent
                                  { RunId = RunId e.RunId
                                    Outcome = Succeeded
                                    DurationMilliseconds = 1000L
                                    Summary = None
                                    Checks = { Passed = 10; Failed = 0; Skipped = 0 } } |> box)

                      let validated: ValidatedEvent =
                          { EventId = EventId e.EventId
                            Source = EventSource e.Source
                            EventType = EventType eventType
                            Subject = sprintf "run/%O" e.RunId
                            ObservedAt = DateTimeOffset.UtcNow
                            InstanceId = InstanceId e.InstanceId
                            EpochId = EpochId e.EpochId
                            Sequence = EventSequence e.Sequence
                            RunId = RunId e.RunId
                            Extensions = Map.empty
                            Event = unbox evt }

                      JournalPosition e.JournalPosition, validated)

              let rebuiltProjections = RunProjection.rebuild events
              let rebuiltProj = rebuiltProjections |> Map.find (RunId runId)

              match incrementalProj with
              | Some incProj ->
                  Expect.equal incProj.State rebuiltProj.State "States should match"
                  Expect.equal incProj.Version rebuiltProj.Version "Versions should match"
              | None -> failwith "Incremental projection should exist"
          } ]
