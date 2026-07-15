namespace Circus.Application

open System
open Circus.Domain

/// Pure classification logic for journal collision outcomes.
/// This module contains no side effects and can be tested in isolation.
module JournalDecision =

    /// Classify a collision between an incoming candidate and existing journal entries.
    /// Envelope equality is only meaningful when identity and sequence resolve to the same row.
    let classifyCollision
        (byIdentity: JournalEntry option)
        (bySequence: JournalEntry option)
        (envelopeEqual: bool)
        : Result<JournalAppendOutcome, PersistenceFailure> =

        match byIdentity, bySequence with
        | None, None ->
            // Should not happen after successful insert - indicates unexpected state
            Error ConstraintClassificationFailed

        | Some identityEntry, None ->
            // Identity exists but sequence doesn't - always a conflict
            Ok(EventIdentityConflict identityEntry.JournalPosition)

        | None, Some sequenceEntry ->
            // Sequence exists but identity doesn't - sequence conflict
            Ok(SequenceConflict sequenceEntry.JournalPosition)

        | Some identityEntry, Some sequenceEntry
            when identityEntry.JournalPosition <> sequenceEntry.JournalPosition ->
            // Both exist but different rows - cross-identity conflict
            Ok(CrossIdentityConflict(identityEntry.JournalPosition, sequenceEntry.JournalPosition))

        | Some identityEntry, Some _ ->
            // Same row - use PostgreSQL envelope equality result for replay detection
            if envelopeEqual then
                Ok(IdempotentReplay identityEntry.JournalPosition)
            else
                Ok(EventIdentityConflict identityEntry.JournalPosition)

    /// Check if the incoming event type is known to the Circus domain.
    /// Unknown event types are journaled but do not affect the run projection.
    let isKnownEventType (eventType: string) : bool =
        match eventType with
        | "io.leamas.execution.started.v1" -> true
        | "io.leamas.execution.finished.v1" -> true
        | _ -> false
