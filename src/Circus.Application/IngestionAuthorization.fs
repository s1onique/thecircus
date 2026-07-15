namespace Circus.Application

open Circus.Domain

/// Principal representing an authenticated producer making an ingestion request.
/// Authentication itself is intentionally supplied by the API composition layer.
type ProducerPrincipal =
    { ProducerId: string
      AllowedInstance: InstanceId option }

/// Failures from the authorization port.  These values are deliberately
/// transport-neutral; the API layer owns their HTTP mapping.
type IngestionAuthorizationFailure =
    | MissingCredentials
    | InvalidCredentials
    | InstanceNotAllowed

module IngestionAuthorizationFailure =
    let toSafeCode failure =
        match failure with
        | MissingCredentials -> "missing_credentials"
        | InvalidCredentials -> "invalid_credentials"
        | InstanceNotAllowed -> "instance_not_allowed"

/// Pure authorization rule applied after the envelope has been decoded.  The
/// port which obtains the principal is an API concern and receives HttpContext
/// there; this module contains no ASP.NET dependency.
module IngestionAuthorization =
    let validateInstanceAuthorization
        (principal: ProducerPrincipal)
        (eventInstance: InstanceId)
        : Result<unit, IngestionAuthorizationFailure> =
        match principal.AllowedInstance with
        | None -> Ok()
        | Some allowed when allowed = eventInstance -> Ok()
        | Some _ -> Error InstanceNotAllowed
