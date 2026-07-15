namespace Circus.Application

open System
open System.Net
open System.Threading.Tasks
open Circus.Contracts
open Circus.Domain

/// Request to ingest a single event.
type IngestEventRequest =
    { Event: ValidatedEvent
      RawBody: byte[]
      EnvelopeJson: string }

/// Result of an ingestion operation.
type IngestEventResult =
    | Success of outcome: JournalAppendOutcome * projection: RunProjection option
    | ContractViolation of violations: NonEmptyList<ContractViolation>
    | AuthorizationFailure of failure: IngestionAuthorizationFailure
    | PersistenceFailure of failure: PersistenceFailure

/// Application service for ingesting events.
/// This is the core orchestration logic that coordinates validation,
/// authorization, and persistence.
type IngestEventService =
    { /// Attempt to ingest a validated event.
      ingest: IngestEventRequest -> Task<IngestEventResult> }

module IngestEvent =

    /// Build a journal candidate from a validated event.
    let buildCandidate (request: IngestEventRequest) : JournalCandidate =
        let evt = request.Event
        { Identity =
            { Source = evt.Source
              EventId = evt.EventId }
          StreamPosition =
            { InstanceId = evt.InstanceId
              EpochId = evt.EpochId
              Sequence = evt.Sequence }
          RunId = evt.RunId
          EventType = evt.EventType
          Subject = evt.Subject
          ObservedAt = evt.ObservedAt
          RawBody = request.RawBody
          EnvelopeJson = request.EnvelopeJson }

    /// Create an ingestion result from a journal append outcome and projection.
    let createSuccessResult
        (outcome: JournalAppendOutcome)
        (projection: RunProjection option)
        : IngestEventResult =
        Success(outcome, projection)

    /// Create a failure result for contract violations.
    let createContractViolationResult (violations: NonEmptyList<ContractViolation>) : IngestEventResult =
        ContractViolation violations

    /// Create a failure result for authorization failures.
    let createAuthorizationFailureResult (failure: IngestionAuthorizationFailure) : IngestEventResult =
        AuthorizationFailure failure

    /// Create a failure result for persistence failures.
    let createPersistenceFailureResult (failure: PersistenceFailure) : IngestEventResult =
        PersistenceFailure failure

    /// Map an ingest result to an HTTP status code.
    let toStatusCode (result: IngestEventResult) : HttpStatusCode =
        match result with
        | Success (Inserted _, _) -> HttpStatusCode.Created
        | Success (IdempotentReplay _, _) -> HttpStatusCode.OK
        | Success (EventIdentityConflict _, _) -> HttpStatusCode.Conflict
        | Success (SequenceConflict _, _) -> HttpStatusCode.Conflict
        | Success (CrossIdentityConflict _, _) -> HttpStatusCode.InternalServerError
        | ContractViolation _ -> HttpStatusCode.UnprocessableEntity
        | AuthorizationFailure MissingCredentials -> HttpStatusCode.Unauthorized
        | AuthorizationFailure InvalidCredentials -> HttpStatusCode.Unauthorized
        | AuthorizationFailure InstanceNotAllowed -> HttpStatusCode.Forbidden
        | PersistenceFailure DatabaseUnavailable -> HttpStatusCode.ServiceUnavailable
        | PersistenceFailure SerializationRetriesExhausted -> HttpStatusCode.ServiceUnavailable
        | PersistenceFailure ConstraintClassificationFailed -> HttpStatusCode.InternalServerError
        | PersistenceFailure ProjectionInvariantFailed -> HttpStatusCode.InternalServerError
        | PersistenceFailure (UnexpectedDatabaseFailure _) -> HttpStatusCode.InternalServerError

    /// Map an ingest result to a problem type URI.
    let toProblemType (result: IngestEventResult) : string =
        match result with
        | Success (Inserted _, _) -> "urn:circus:problem:event-inserted"
        | Success (IdempotentReplay _, _) -> "urn:circus:problem:idempotent-replay"
        | Success (EventIdentityConflict _, _) -> "urn:circus:problem:event-identity-conflict"
        | Success (SequenceConflict _, _) -> "urn:circus:problem:event-sequence-conflict"
        | Success (CrossIdentityConflict _, _) -> "urn:circus:problem:cross-identity-conflict"
        | ContractViolation _ -> "urn:circus:problem:contract-violation"
        | AuthorizationFailure _ -> "urn:circus:problem:authorization-failure"
        | PersistenceFailure _ -> "urn:circus:problem:persistence-failure"

    /// Map an ingest result to a problem title.
    let toProblemTitle (result: IngestEventResult) : string =
        match result with
        | Success (Inserted _, _) -> "Event inserted"
        | Success (IdempotentReplay _, _) -> "Idempotent replay"
        | Success (EventIdentityConflict _, _) -> "Event identity conflict"
        | Success (SequenceConflict _, _) -> "Event sequence conflict"
        | Success (CrossIdentityConflict _, _) -> "Cross-identity conflict"
        | ContractViolation _ -> "Contract violation"
        | AuthorizationFailure _ -> "Authorization failure"
        | PersistenceFailure _ -> "Persistence failure"

    /// Map an ingest result to a low-cardinality error code.
    let toErrorCode (result: IngestEventResult) : string =
        match result with
        | Success (Inserted _, _) -> "inserted"
        | Success (IdempotentReplay _, _) -> "idempotent_replay"
        | Success (EventIdentityConflict _, _) -> "event_identity_conflict"
        | Success (SequenceConflict _, _) -> "event_sequence_conflict"
        | Success (CrossIdentityConflict _, _) -> "cross_identity_conflict"
        | ContractViolation _ -> "contract_violation"
        | AuthorizationFailure MissingCredentials -> "missing_credentials"
        | AuthorizationFailure InvalidCredentials -> "invalid_credentials"
        | AuthorizationFailure InstanceNotAllowed -> "instance_not_allowed"
        | PersistenceFailure DatabaseUnavailable -> "database_unavailable"
        | PersistenceFailure SerializationRetriesExhausted -> "retries_exhausted"
        | PersistenceFailure ConstraintClassificationFailed -> "classification_failed"
        | PersistenceFailure ProjectionInvariantFailed -> "invariant_failed"
        | PersistenceFailure (UnexpectedDatabaseFailure _) -> "unexpected_failure"
