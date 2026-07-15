namespace Circus.Application

open System.Threading.Tasks
open Circus.Contracts
open Circus.Domain

/// Request passed from the HTTP boundary to the application service.  The
/// bytes are the accepted request body and EnvelopeJson is the semantic JSON
/// representation used by persistence for replay equality.
type IngestEventRequest =
    { Event: ValidatedEvent
      RawBody: byte[]
      EnvelopeJson: string }

/// Result of one complete ingestion attempt.  HTTP status codes are not part
/// of this type; the API composition layer maps these stable application
/// outcomes to its public contract.
type IngestEventResult =
    | Success of outcome: JournalAppendOutcome * projection: RunProjection option
    | ContractViolation of violations: NonEmptyList<ContractViolation>
    | AuthorizationFailure of failure: IngestionAuthorizationFailure
    | PersistenceFailure of failure: PersistenceFailure

/// Application ingestion port.  The PostgreSQL adapter supplies the real
/// implementation; API tests can provide a deterministic function.
type IngestEventService =
    { Ingest: IngestEventRequest -> Task<IngestEventResult> }

module IngestEvent =
    /// Build the persistence candidate without changing any domain values.
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

    let createSuccessResult outcome projection = Success(outcome, projection)
    let createContractViolationResult violations = ContractViolation violations
    let createAuthorizationFailureResult failure = AuthorizationFailure failure
    let createPersistenceFailureResult failure = PersistenceFailure failure
