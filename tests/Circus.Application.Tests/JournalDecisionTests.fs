module Circus.Application.Tests.JournalDecisionTests

open System
open Expecto
open Circus.Application
open Circus.Domain

/// Helper to create a test journal entry.
let createTestEntry
    (position: int64)
    (source: string)
    (eventId: string)
    (instanceId: string)
    (epochId: Guid)
    (sequence: int64)
    (runId: Guid)
    (envelope: string)
    =
    { JournalPosition = JournalPosition position
      Source = source
      EventId = eventId
      InstanceId = instanceId
      EpochId = epochId
      Sequence = sequence
      RunId = runId
      EventType = "io.leamas.execution.started.v1"
      EnvelopeJson = envelope
      RawBody = System.Text.Encoding.UTF8.GetBytes envelope }

let tests =
    testList
        "JournalDecision"
        [ test "classifyCollision: error when no conflicts (should not happen)" {
              let result = JournalDecision.classifyCollision None None true

              match result with
              | Error ConstraintClassificationFailed -> ()
              | _ -> failwithf "Expected Error ConstraintClassificationFailed, got %A" result
          }

          test "classifyCollision: identity conflict when identity exists but sequence doesn't" {
              let existing =
                  createTestEntry 42L "source1" "id1" "inst1" (Guid.NewGuid()) 1L (Guid.NewGuid()) "{}"

              let result = JournalDecision.classifyCollision (Some existing) None true

              match result with
              | Ok(EventIdentityConflict(JournalPosition 42L)) -> ()
              | _ -> failwithf "Expected Ok EventIdentityConflict, got %A" result
          }

          test "classifyCollision: sequence conflict when identity doesn't exist" {
              let existing =
                  createTestEntry 42L "source2" "id2" "inst1" (Guid.NewGuid()) 1L (Guid.NewGuid()) "{}"

              let result = JournalDecision.classifyCollision None (Some existing) true

              match result with
              | Ok(SequenceConflict(JournalPosition 42L)) -> ()
              | _ -> failwithf "Expected Ok SequenceConflict, got %A" result
          }

          test "classifyCollision: cross-identity conflict when different rows" {
              let byIdentity =
                  createTestEntry 42L "source1" "id1" "inst1" (Guid.NewGuid()) 1L (Guid.NewGuid()) "{}"

              let bySequence =
                  createTestEntry 99L "source2" "id2" "inst1" (Guid.NewGuid()) 1L (Guid.NewGuid()) "{}"

              let result =
                  JournalDecision.classifyCollision (Some byIdentity) (Some bySequence) true

              match result with
              | Ok(CrossIdentityConflict(JournalPosition 42L, JournalPosition 99L)) -> ()
              | _ -> failwithf "Expected Ok CrossIdentityConflict, got %A" result
          }

          test "classifyCollision: idempotent replay when same row and envelope matches" {
              let existing =
                  createTestEntry 42L "source1" "id1" "inst1" (Guid.NewGuid()) 1L (Guid.NewGuid()) "{}"

              let result = JournalDecision.classifyCollision (Some existing) (Some existing) true

              match result with
              | Ok(IdempotentReplay(JournalPosition 42L)) -> ()
              | _ -> failwithf "Expected Ok IdempotentReplay, got %A" result
          }

          test "classifyCollision: identity conflict when same row but envelope differs" {
              let existing =
                  createTestEntry 42L "source1" "id1" "inst1" (Guid.NewGuid()) 1L (Guid.NewGuid()) "{}"

              let result = JournalDecision.classifyCollision (Some existing) (Some existing) false

              match result with
              | Ok(EventIdentityConflict(JournalPosition 42L)) -> ()
              | _ -> failwithf "Expected Ok EventIdentityConflict, got %A" result
          }

          test "isKnownEventType: started is known" {
              Expect.isTrue
                  (JournalDecision.isKnownEventType "io.leamas.execution.started.v1")
                  "Started should be known"
          }

          test "isKnownEventType: finished is known" {
              Expect.isTrue
                  (JournalDecision.isKnownEventType "io.leamas.execution.finished.v1")
                  "Finished should be known"
          }

          test "isKnownEventType: unknown event type" {
              Expect.isFalse
                  (JournalDecision.isKnownEventType "io.leamas.execution.artefact.published.v3")
                  "Unknown should not be known"
          } ]
