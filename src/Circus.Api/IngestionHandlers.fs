module Circus.Api.IngestionHandlers

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open Circus.Application
open Circus.Application.IngestEvent
open Circus.Application.JournalDecision
open Circus.Application.IngestionAuthorization
open Circus.Contracts
open Circus.Contracts.CloudEventSpec
open Circus.Domain
open Circus.Persistence.Postgres
open Giraffe

/// Problem Details response per RFC 9457.
type ProblemDetails =
    { [<JsonPropertyName("type")>]
      Type: string
      [<JsonPropertyName("title")>]
      Title: string
      [<JsonPropertyName("status")>]
      Status: int
      [<JsonPropertyName("code")>]
      Code: string }

module ProblemDetails =
    let create (problemType: string) (title: string) (status: int) (code: string) =
        { Type = problemType
          Title = title
          Status = status
          Code = code }

/// Response body for successful insert.
type InsertResponse =
    { [<JsonPropertyName("result")>]
      Result: string
      [<JsonPropertyName("journal_position")>]
      JournalPosition: int64 }

/// Response body for replay.
type ReplayResponse =
    { [<JsonPropertyName("result")>]
      Result: string
      [<JsonPropertyName("journal_position")>]
      JournalPosition: int64 }

/// Contract violation detail.
type ViolationDetail =
    { [<JsonPropertyName("type")>]
      ViolationType: string
      [<JsonPropertyName("field")>]
      Field: string option
      [<JsonPropertyName("message")>]
      Message: string }

/// Response body for contract violations.
type ContractViolationResponse =
    { [<JsonPropertyName("type")>]
      Type: string
      [<JsonPropertyName("title")>]
      Title: string
      [<JsonPropertyName("status")>]
      Status: int
      [<JsonPropertyName("code")>]
      Code: string
      [<JsonPropertyName("violations")>]
      Violations: ViolationDetail list }

module ContractViolationResponse =
    let fromViolations (violations: NonEmptyList<ContractViolation>) =
        let details =
            violations
            |> NonEmptyList.toList
            |> List.map (fun v ->
                match v with
                | BodyTooLarge (max, actual) ->
                    { ViolationType = "body_too_large"
                      Field = None
                      Message = sprintf "Body size %d exceeds maximum %d" actual max }
                | MalformedJson msg ->
                    { ViolationType = "malformed_json"
                      Field = None
                      Message = msg }
                | RootMustBeObject ->
                    { ViolationType = "root_must_be_object"
                      Field = None
                      Message = "Root element must be a JSON object" }
                | MissingField name ->
                    { ViolationType = "missing_field"
                      Field = Some name
                      Message = sprintf "Required field '%s' is missing" name }
                | InvalidFieldType (name, expected) ->
                    { ViolationType = "invalid_field_type"
                      Field = Some name
                      Message = sprintf "Field '%s' must be of type %s" name expected }
                | InvalidFieldValue (name, reason) ->
                    { ViolationType = "invalid_field_value"
                      Field = Some name
                      Message = sprintf "Field '%s': %s" name reason }
                | UnsupportedSpecVersion v ->
                    { ViolationType = "unsupported_spec_version"
                      Field = Some "specversion"
                      Message = sprintf "Unsupported specversion: %s" v }
                | SubjectRunIdMismatch ->
                    { ViolationType = "subject_run_id_mismatch"
                      Field = Some "subject"
                      Message = "Subject must match runid" }
                | DuplicateField name ->
                    { ViolationType = "duplicate_field"
                      Field = Some name
                      Message = sprintf "Field '%s' appears more than once" name }
                | InvalidExtensionName name ->
                    { ViolationType = "invalid_extension_name"
                      Field = Some name
                      Message = sprintf "Extension name '%s' is invalid" name }
                | InvalidKnownPayload (eventType, violations) ->
                    { ViolationType = "invalid_known_payload"
                      Field = Some "data"
                      Message = sprintf "Invalid payload for event type '%s'" eventType })

        { Type = "urn:circus:problem:contract-violation"
          Title = "Contract violation"
          Status = 422
          Code = "contract_violation"
          Violations = details }

/// Maximum body size for ingestion.
let private maxBodySize = EventDecoder.DefaultMaximumBytes + 1

/// JSON serialization options for the API.
let private jsonOptions =
    let opts = JsonSerializerOptions()
    opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    opts

/// Write a problem details response and return.
let writeProblemAndReturn (ctx: HttpContext) (problem: ProblemDetails) =
    task {
        ctx.Response.StatusCode <- problem.Status
        ctx.Response.ContentType <- "application/problem+json"
        do! ctx.Response.WriteAsync(JsonSerializer.Serialize(problem, jsonOptions))
        return Some ctx
    }

/// Write an insert response.
let writeInsertResponse (ctx: HttpContext) (position: int64) =
    task {
        ctx.Response.StatusCode <- 201
        ctx.Response.ContentType <- "application/json"
        let source = ctx.Request.RouteValues["source"] |> string
        let eventId = ctx.Request.RouteValues["id"] |> string
        ctx.Response.Headers.Location <- $"/api/v1/events/{Uri.EscapeDataString(source)}/{Uri.EscapeDataString(eventId)}"
        let body = { InsertResponse.Result = "inserted"; JournalPosition = position }
        do! ctx.Response.WriteAsync(JsonSerializer.Serialize(body, jsonOptions))
        return Some ctx
    }

/// Write a replay response.
let writeReplayResponse (ctx: HttpContext) (position: int64) =
    task {
        ctx.Response.StatusCode <- 200
        ctx.Response.ContentType <- "application/json"
        let body = { ReplayResponse.Result = "idempotent_replay"; JournalPosition = position }
        do! ctx.Response.WriteAsync(JsonSerializer.Serialize(body, jsonOptions))
        return Some ctx
    }

/// Write a contract violation response.
let writeContractViolationAndReturn (ctx: HttpContext) (violations: NonEmptyList<ContractViolation>) =
    task {
        ctx.Response.StatusCode <- 422
        ctx.Response.ContentType <- "application/problem+json"
        let response = ContractViolationResponse.fromViolations violations
        do! ctx.Response.WriteAsync(JsonSerializer.Serialize(response, jsonOptions))
        return Some ctx
    }

/// POST /api/v1/events handler.
/// Requires Content-Type: application/cloudevents+json
let ingestEventHandler (dataSource: Npgsql.NpgsqlDataSource) : HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) ->
        task {
            // 1. Authorize first (before reading body)
            let authPort: IngestionAuthorizationPort = AuthorizationAdapters.denyAllAuthorization
            let! authResult = authPort ()

            match authResult with
            | Error failure ->
                let problem =
                    match failure with
                    | MissingCredentials ->
                        ProblemDetails.create
                            "urn:circus:problem:authorization-failure"
                            "Authorization required"
                            401
                            "missing_credentials"
                    | InvalidCredentials ->
                        ProblemDetails.create
                            "urn:circus:problem:authorization-failure"
                            "Invalid credentials"
                            401
                            "invalid_credentials"
                    | InstanceNotAllowed ->
                        ProblemDetails.create
                            "urn:circus:problem:authorization-failure"
                            "Instance not allowed"
                            403
                            "instance_not_allowed"

                return! writeProblemAndReturn ctx problem

            | Ok principal ->
                // 2. Require structured CloudEvents media type
                let contentType = ctx.Request.ContentType
                if contentType <> StructuredJsonContentType then
                    let problem =
                        ProblemDetails.create
                            "urn:circus:problem:unsupported-media-type"
                            "Unsupported Media Type"
                            415
                            "unsupported_content_type"

                    return! writeProblemAndReturn ctx problem
                else
                    // 3. Read bounded body
                    use memoryStream = new MemoryStream()
                    let buffer = Array.zeroCreate 8192
                    let mutable totalBytes = 0
                    let mutable tooLarge = false

                    use readStream = ctx.Request.Body
                    while not tooLarge do
                        let! bytesRead = readStream.ReadAsync(buffer, 0, buffer.Length)
                        if bytesRead = 0 then
                            tooLarge <- true // Signal end of stream
                        else
                            totalBytes <- totalBytes + bytesRead
                            if totalBytes > maxBodySize then
                                tooLarge <- true
                            else
                                do! memoryStream.WriteAsync(buffer, 0, bytesRead)

                    if totalBytes > EventDecoder.DefaultMaximumBytes then
                        let problem =
                            ProblemDetails.create
                                "urn:circus:problem:payload-too-large"
                                "Payload Too Large"
                                413
                                "body_too_large"

                        return! writeProblemAndReturn ctx problem
                    else
                        // 4. Decode the event
                        let body = memoryStream.ToArray()
                        let bodyMemory = ReadOnlyMemory<_>(body)

                        match EventDecoder.decode EventDecoder.DefaultMaximumBytes bodyMemory with
                        | Error violations ->
                            return! writeContractViolationAndReturn ctx violations

                        | Ok validatedEvent ->
                            // 5. Validate instance authorization
                            match IngestionAuthorization.validateInstanceAuthorization principal validatedEvent.InstanceId with
                            | Error _ ->
                                let problem =
                                    ProblemDetails.create
                                        "urn:circus:problem:authorization-failure"
                                        "Instance not allowed"
                                        403
                                        "instance_not_allowed"

                                return! writeProblemAndReturn ctx problem

                            | Ok () ->
                                // 6. Normalize JSON for storage
                                use doc = JsonDocument.Parse(body, JsonDocumentOptions())
                                let normalizedJson = doc.RootElement.GetRawText()

                                // 7. Execute ingestion transaction
                                let ingestService = IngestEventService.create dataSource

                                let candidate = buildCandidate { Event = validatedEvent; RawBody = body; EnvelopeJson = normalizedJson }
                                let! txResult = ingestService.Ingest candidate validatedEvent

                                match txResult with
                                | AppendSucceeded (outcome, _) ->
                                    match outcome with
                                    | Inserted pos ->
                                        return! writeInsertResponse ctx (JournalPosition.value pos)
                                    | IdempotentReplay pos ->
                                        return! writeReplayResponse ctx (JournalPosition.value pos)
                                    | EventIdentityConflict _ ->
                                        let problem =
                                            ProblemDetails.create
                                                "urn:circus:problem:event-identity-conflict"
                                                "Event identity conflict"
                                                409
                                                "event_identity_conflict"

                                        return! writeProblemAndReturn ctx problem
                                    | SequenceConflict _ ->
                                        let problem =
                                            ProblemDetails.create
                                                "urn:circus:problem:event-sequence-conflict"
                                                "Event sequence conflict"
                                                409
                                                "event_sequence_conflict"

                                        return! writeProblemAndReturn ctx problem
                                    | CrossIdentityConflict _ ->
                                        let problem =
                                            ProblemDetails.create
                                                "urn:circus:problem:cross-identity-conflict"
                                                "Cross-identity conflict"
                                                500
                                                "cross_identity_conflict"

                                        return! writeProblemAndReturn ctx problem

                                | AppendFailed failure ->
                                    let problem =
                                        match failure with
                                        | DatabaseUnavailable ->
                                            ProblemDetails.create
                                                "urn:circus:problem:persistence-failure"
                                                "Service Unavailable"
                                                503
                                                "database_unavailable"
                                        | SerializationRetriesExhausted ->
                                            ProblemDetails.create
                                                "urn:circus:problem:persistence-failure"
                                                "Service Unavailable"
                                                503
                                                "retries_exhausted"
                                        | ConstraintClassificationFailed ->
                                            ProblemDetails.create
                                                "urn:circus:problem:internal-error"
                                                "Internal Server Error"
                                                500
                                                "classification_failed"
                                        | ProjectionInvariantFailed ->
                                            ProblemDetails.create
                                                "urn:circus:problem:internal-error"
                                                "Internal Server Error"
                                                500
                                                "invariant_failed"
                                        | UnexpectedDatabaseFailure _ ->
                                            ProblemDetails.create
                                                "urn:circus:problem:internal-error"
                                                "Internal Server Error"
                                                500
                                                "unexpected_failure"

                                    return! writeProblemAndReturn ctx problem
        }
