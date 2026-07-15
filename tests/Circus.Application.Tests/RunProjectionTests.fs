module Circus.Application.Tests.RunProjectionTests

open System
open Expecto
open Circus.Application
open Circus.Contracts
open Circus.Domain

/// Helper to create a valid started event.
let createStartedEvent (runId: Guid) (instanceId: string) (epochId: Guid) (sequence: int64) =
    let runIdVal = (RunId.tryCreate runId).Value
    let validated: ValidatedEvent =
        { EventId = (EventId.tryCreate "event-id").Value
          Source = (EventSource.tryCreate "urn:leamas:instance:test").Value
          EventType = (EventType.tryCreate "io.leamas.execution.started.v1").Value
          Subject = sprintf "run/%O" runId
          ObservedAt = DateTimeOffset.UtcNow
          InstanceId = (InstanceId.tryCreate instanceId).Value
          EpochId = (EpochId.tryCreate epochId).Value
          Sequence = (EventSequence.tryCreate sequence).Value
          RunId = runIdVal
          Extensions = Map.empty
          Event =
              ExecutionStartedEvent
                  { RunId = runIdVal
                    Repository = (RepositoryRef.tryCreate "test-repo").Value
                    ActId = None
                    LeamasVersion = (LeamasVersion.tryCreate "1.0.0").Value
                    GitRevision = None
                    StartedBy = None } }
    validated

/// Helper to create a valid finished event.
let createFinishedEvent (runId: Guid) (instanceId: string) (epochId: Guid) (sequence: int64) (outcome: ExecutionOutcome) =
    let runIdVal = (RunId.tryCreate runId).Value
    let validated: ValidatedEvent =
        { EventId = (EventId.tryCreate "event-id").Value
          Source = (EventSource.tryCreate "urn:leamas:instance:test").Value
          EventType = (EventType.tryCreate "io.leamas.execution.finished.v1").Value
          Subject = sprintf "run/%O" runId
          ObservedAt = DateTimeOffset.UtcNow
          InstanceId = (InstanceId.tryCreate instanceId).Value
          EpochId = (EpochId.tryCreate epochId).Value
          Sequence = (EventSequence.tryCreate sequence).Value
          RunId = runIdVal
          Extensions = Map.empty
          Event =
              ExecutionFinishedEvent
                  { RunId = runIdVal
                    Outcome = outcome
                    DurationMilliseconds = 1000L
                    Summary = None
                    Checks = { Passed = 10; Failed = 0; Skipped = 0 } } }
    validated

/// Helper to create an unknown event.
let createUnknownEvent (runId: Guid) (instanceId: string) (epochId: Guid) (sequence: int64) =
    let runIdVal = (RunId.tryCreate runId).Value
    let validated: ValidatedEvent =
        { EventId = (EventId.tryCreate "event-id").Value
          Source = (EventSource.tryCreate "urn:leamas:instance:test").Value
          EventType = (EventType.tryCreate "io.leamas.execution.artefact.published.v3").Value
          Subject = sprintf "run/%O" runId
          ObservedAt = DateTimeOffset.UtcNow
          InstanceId = (InstanceId.tryCreate instanceId).Value
          EpochId = (EpochId.tryCreate epochId).Value
          Sequence = (EventSequence.tryCreate sequence).Value
          RunId = runIdVal
          Extensions = Map.empty
          Event =
              UnrecognizedEvent
                  { EventType = "io.leamas.execution.artefact.published.v3"
                    Data = None } }
    validated

let tests =
    testList
        "RunProjection"
        [ test "applyEvent: started only creates StartedOnly state" {
              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()
              let event = createStartedEvent runId "inst1" epochId 1L

              let result = RunProjection.applyEvent None (JournalPosition 1L) event

              match result with
              | Some proj ->
                  Expect.equal proj.State StartedOnly "State should be StartedOnly"
                  Expect.equal proj.RunId ((RunId.tryCreate runId).Value) "RunId should match"
                  Expect.isSome proj.StartedEvent "StartedEvent should be set"
                  Expect.isNone proj.FinishedEvent "FinishedEvent should not be set"
              | None -> failwith "Expected Some projection"
          }

          test "applyEvent: finished only creates FinishedWithoutStart state" {
              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()
              let event = createFinishedEvent runId "inst1" epochId 1L ExecutionOutcome.Succeeded

              let result = RunProjection.applyEvent None (JournalPosition 1L) event

              match result with
              | Some proj ->
                  Expect.equal proj.State FinishedWithoutStart "State should be FinishedWithoutStart"
                  Expect.isNone proj.StartedEvent "StartedEvent should not be set"
                  Expect.isSome proj.FinishedEvent "FinishedEvent should be set"
              | None -> failwith "Expected Some projection"
          }

          test "applyEvent: started then finished creates Completed state" {
              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()

              let started = createStartedEvent runId "inst1" epochId 1L
              let finished = createFinishedEvent runId "inst1" epochId 2L ExecutionOutcome.Succeeded

              let afterStarted = RunProjection.applyEvent None (JournalPosition 1L) started
              let afterFinished = RunProjection.applyEvent afterStarted (JournalPosition 2L) finished

              match afterFinished with
              | Some proj ->
                  Expect.equal proj.State Completed "State should be Completed"
                  Expect.equal proj.Outcome (Some ExecutionOutcome.Succeeded) "Outcome should be Succeeded"
              | None -> failwith "Expected Some projection"
          }

          test "applyEvent: finished then started creates Completed state" {
              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()

              let finished = createFinishedEvent runId "inst1" epochId 1L ExecutionOutcome.Failed
              let started = createStartedEvent runId "inst1" epochId 2L

              let afterFinished = RunProjection.applyEvent None (JournalPosition 1L) finished
              let afterStarted = RunProjection.applyEvent afterFinished (JournalPosition 2L) started

              match afterStarted with
              | Some proj ->
                  Expect.equal proj.State Completed "State should be Completed"
                  Expect.equal proj.Outcome (Some ExecutionOutcome.Failed) "Outcome should be Failed"
                  Expect.isSome proj.StartedEvent "StartedEvent should be set"
                  Expect.isSome proj.FinishedEvent "FinishedEvent should be set"
              | None -> failwith "Expected Some projection"
          }

          test "applyEvent: second started marks conflict" {
              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()

              let started1 = createStartedEvent runId "inst1" epochId 1L
              let started2 = createStartedEvent runId "inst1" epochId 2L

              let afterFirst = RunProjection.applyEvent None (JournalPosition 1L) started1
              let afterSecond = RunProjection.applyEvent afterFirst (JournalPosition 2L) started2

              match afterSecond with
              | Some proj ->
                  Expect.equal proj.State Conflicted "State should be Conflicted"
                  Expect.equal proj.ConflictCount 1 "ConflictCount should be 1"
                  // First authority is preserved
                  Expect.equal proj.StartedEvent (Some(JournalPosition 1L)) "First started should be preserved"
              | None -> failwith "Expected Some projection"
          }

          test "applyEvent: unknown event does not create projection" {
              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()
              let unknown = createUnknownEvent runId "inst1" epochId 1L

              let result = RunProjection.applyEvent None (JournalPosition 1L) unknown

              Expect.isNone result "Unknown event should not create projection"
          }

          test "applyEvent: unknown event does not mutate existing projection" {
              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()

              let started = createStartedEvent runId "inst1" epochId 1L
              let unknown = createUnknownEvent runId "inst1" epochId 2L

              let afterStarted = RunProjection.applyEvent None (JournalPosition 1L) started
              let afterUnknown = RunProjection.applyEvent afterStarted (JournalPosition 2L) unknown

              match afterUnknown with
              | Some proj ->
                  // Projection should be unchanged
                  Expect.equal proj.State StartedOnly "State should still be StartedOnly"
                  Expect.equal proj.ConflictCount 0 "ConflictCount should still be 0"
              | None -> failwith "Expected Some projection"
          }

          test "rebuild: from journal order equals incremental" {
              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()
              let runIdVal = (RunId.tryCreate runId).Value

              let events =
                  [ (JournalPosition 1L, createStartedEvent runId "inst1" epochId 1L)
                    (JournalPosition 2L, createFinishedEvent runId "inst1" epochId 2L ExecutionOutcome.Succeeded) ]

              let rebuilt = RunProjection.rebuild events

              Expect.equal (Map.count rebuilt) 1 "Should have exactly one run projection"

              match Map.tryFind runIdVal rebuilt with
              | Some proj ->
                  Expect.equal proj.State Completed "Rebuilt projection should be Completed"
                  Expect.equal proj.ConflictCount 0 "No conflicts"
              | None -> failwith "Expected projection for runId"
          }

          test "version increments on each event" {
              let runId = Guid.NewGuid()
              let epochId = Guid.NewGuid()

              let started = createStartedEvent runId "inst1" epochId 1L

              let afterFirst = RunProjection.applyEvent None (JournalPosition 1L) started

              match afterFirst with
              | Some proj ->
                  Expect.equal proj.Version 2L "Version should be 2 after first event"
              | None -> failwith "Expected Some projection"
          } ]
