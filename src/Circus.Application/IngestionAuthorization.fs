namespace Circus.Application

open System.Threading.Tasks
open Circus.Domain

/// Principal representing an authenticated producer making ingestion requests.
type ProducerPrincipal =
    { ProducerId: string
      AllowedInstance: InstanceId option }

/// Failures from the ingestion authorization process.
type IngestionAuthorizationFailure =
    | MissingCredentials
    | InvalidCredentials
    | InstanceNotAllowed

module IngestionAuthorizationFailure =
    /// Returns a safe error code for logging.
    let toSafeCode (failure: IngestionAuthorizationFailure) : string =
        match failure with
        | MissingCredentials -> "missing_credentials"
        | InvalidCredentials -> "invalid_credentials"
        | InstanceNotAllowed -> "instance_not_allowed"

/// Authorization port that extracts the producer principal.
/// The actual HTTP context extraction is implemented in the API layer.
type IngestionAuthorizationPort =
    unit -> Task<Result<ProducerPrincipal, IngestionAuthorizationFailure>>

/// Production authorization adapters.
module AuthorizationAdapters =

    /// Production authorization adapter that rejects all requests.
    /// This implements the "no anonymous ingestion" requirement by default.
    let denyAllAuthorization: IngestionAuthorizationPort =
        fun () -> Task.FromResult(Error MissingCredentials)

    /// Authorization adapter that allows all requests (for testing).
    let allowAllAuthorization (producerId: string) (instanceId: InstanceId option): IngestionAuthorizationPort =
        fun () -> Task.FromResult(Ok { ProducerId = producerId; AllowedInstance = instanceId })

/// Authorization validation logic.
module IngestionAuthorization =
    /// Validate that the validated event's instance matches the principal's allowed instance.
    let validateInstanceAuthorization
        (principal: ProducerPrincipal)
        (eventInstance: InstanceId)
        : Result<unit, IngestionAuthorizationFailure> =
        match principal.AllowedInstance with
        | None -> Ok() // Principal is not instance-restricted
        | Some allowed when allowed = eventInstance -> Ok()
        | Some _ -> Error InstanceNotAllowed
