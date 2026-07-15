namespace Circus.Application

open System
open Circus.Domain

/// Journal position is the monotonically increasing identity assigned by the
/// database's IDENTITY column. It is the authoritative ordering of events.
type JournalPosition = JournalPosition of int64

module JournalPosition =
    let value (JournalPosition p) = p
    let initial = JournalPosition 1L

/// Journal identity is the CloudEvents identity pair that uniquely identifies
/// an event in the Circus system.
type JournalIdentity =
    { Source: EventSource
      EventId: EventId }

module JournalIdentity =
    let toTuple identity = (EventSource.value identity.Source, EventId.value identity.EventId)

/// Stream position defines the ordering within a specific instance/epoch pair.
type StreamPosition =
    { InstanceId: InstanceId
      EpochId: EpochId
      Sequence: EventSequence }

module StreamPosition =
    let toTuple pos =
        (InstanceId.value pos.InstanceId, EpochId.value pos.EpochId, EventSequence.value pos.Sequence)

/// A candidate event ready to be appended to the journal.
type JournalCandidate =
    { Identity: JournalIdentity
      StreamPosition: StreamPosition
      RunId: RunId
      EventType: EventType
      Subject: string
      ObservedAt: DateTimeOffset
      RawBody: byte[]
      EnvelopeJson: string }

/// A row retrieved from the journal during conflict classification.
type JournalEntry =
    { JournalPosition: JournalPosition
      Source: string
      EventId: string
      InstanceId: string
      EpochId: Guid
      Sequence: int64
      RunId: Guid
      EventType: string
      EnvelopeJson: string }

/// Possible outcomes from attempting to append an event to the journal.
/// These are typed domain outcomes, not exceptions.
type JournalAppendOutcome =
    | Inserted of JournalPosition
    | IdempotentReplay of ExistingPosition: JournalPosition
    | EventIdentityConflict of ExistingPosition: JournalPosition
    | SequenceConflict of ExistingPosition: JournalPosition
    | CrossIdentityConflict of EventIdentityPosition: JournalPosition * SequencePosition: JournalPosition

module JournalAppendOutcome =
    /// True when the outcome represents a successful append.
    let isSuccess outcome =
        match outcome with
        | Inserted _ -> true
        | _ -> false

    /// Get the journal position from an outcome, if one exists.
    let position outcome =
        match outcome with
        | Inserted pos -> Some pos
        | IdempotentReplay pos -> Some pos
        | EventIdentityConflict pos -> Some pos
        | SequenceConflict pos -> Some pos
        | CrossIdentityConflict (pos1, _) -> Some pos1

/// Transaction retry attempt counter.
type TransactionAttempt =
    | First
    | Retry of number: int

module TransactionAttempt =
    let next attempt =
        match attempt with
        | First -> Retry 1
        | Retry n -> Retry(n + 1)

    let toInt attempt =
        match attempt with
        | First -> 0
        | Retry n -> n

/// Failures that can occur during persistence operations.
/// Raw SQL, connection strings, and exception details are never exposed.
type PersistenceFailure =
    | DatabaseUnavailable
    | SerializationRetriesExhausted
    | ConstraintClassificationFailed
    | ProjectionInvariantFailed
    | UnexpectedDatabaseFailure of SafeCode: string

module PersistenceFailure =
    /// Returns a safe error code that can be logged without exposing internals.
    let toSafeCode failure =
        match failure with
        | DatabaseUnavailable -> "database_unavailable"
        | SerializationRetriesExhausted -> "serialization_retries_exhausted"
        | ConstraintClassificationFailed -> "constraint_classification_failed"
        | ProjectionInvariantFailed -> "projection_invariant_failed"
        | UnexpectedDatabaseFailure _ -> "unexpected_database_failure"
