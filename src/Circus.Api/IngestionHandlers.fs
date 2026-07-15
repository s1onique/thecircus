module Circus.Api.IngestionHandlers

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Net.Http.Headers
open Giraffe
open Circus.Application
open Circus.Application.IngestEvent
open Circus.Application.IngestionAuthorization
open Circus.Contracts
open Circus.Domain

/// API-layer ports.  Authorization receives the live request context but the
/// application project remains unaware of ASP.NET Core.
type AuthorizationPort = HttpContext -> Task<Result<ProducerPrincipal, IngestionAuthorizationFailure>>
type IngestionPort = IngestEventRequest -> Task<IngestEventResult>

type PublicError =
    { [<JsonPropertyName("error")>]
      Error: string
      [<JsonPropertyName("code")>]
      Code: string }

type PublicSuccess =
    { [<JsonPropertyName("result")>]
      Result: string }

module AuthorizationAdapters =
    /// The initial production adapter deliberately rejects every request.  It
    /// still receives HttpContext so the later producer-auth ACT can inspect
    /// request-specific information without changing the handler seam.
    let denyAll: AuthorizationPort = fun _ -> Task.FromResult(Error MissingCredentials)

let private maxBodySize = EventDecoder.DefaultMaximumBytes
let requestBodyLimit = maxBodySize

let private jsonOptions =
    let options = JsonSerializerOptions()
    options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    options

let private writeJson (ctx: HttpContext) (status: int) (value: obj) =
    task {
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/problem+json"
        do! ctx.Response.WriteAsync(JsonSerializer.Serialize(value, jsonOptions), ctx.RequestAborted)
        return Some ctx
    }

let private error status code =
    let value: PublicError =
        { Error = "request_failed"
          Code = code }

    status, (value :> obj)

let private writeError (ctx: HttpContext) status code =
    let statusCode, body = error status code
    writeJson ctx statusCode body

let private writeSuccess (ctx: HttpContext) status result =
    task {
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsync(JsonSerializer.Serialize({ Result = result }, jsonOptions), ctx.RequestAborted)
        return Some ctx
    }

let private supportedMediaType (value: string) =
    if String.IsNullOrWhiteSpace value then
        false
    else
        let mutable parsed = Unchecked.defaultof<MediaTypeHeaderValue>

        if not (MediaTypeHeaderValue.TryParse(value, &parsed)) then
            false
        else
            let mediaType = parsed.MediaType.Value

            mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || (mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
                && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))

let private readBoundedBody (ctx: HttpContext) : Task<byte[] option> =
    task {
        if
            ctx.Request.ContentLength.HasValue
            && ctx.Request.ContentLength.Value > int64 maxBodySize
        then
            return None
        else
            use stream = new MemoryStream(maxBodySize)
            let buffer = Array.zeroCreate<byte> 8192
            let mutable total = 0L
            let mutable tooLarge = false
            let mutable finished = false

            while not tooLarge && not finished do
                let! read = ctx.Request.Body.ReadAsync(buffer, 0, buffer.Length, ctx.RequestAborted)

                if read = 0 then
                    finished <- true
                elif total + int64 read > int64 maxBodySize then
                    tooLarge <- true
                else
                    total <- total + int64 read
                    do! stream.WriteAsync(buffer, 0, read, ctx.RequestAborted)

            if tooLarge then
                return None
            else
                return Some(stream.ToArray())
    }

let private violationStatus (violations: NonEmptyList<ContractViolation>) =
    let values = NonEmptyList.toList violations

    if
        values
        |> List.exists (function
            | BodyTooLarge _ -> true
            | _ -> false)
    then
        413, "body_too_large"
    elif
        values
        |> List.exists (function
            | MalformedJson _ -> true
            | DuplicateField _ -> true
            | _ -> false)
    then
        400,
        if
            values
            |> List.exists (function
                | DuplicateField _ -> true
                | _ -> false)
        then
            "duplicate_json_property"
        else
            "malformed_json"
    else
        422, "contract_violation"

let private locationFor (event: ValidatedEvent) =
    let source = Uri.EscapeDataString(EventSource.value event.Source)
    let eventId = Uri.EscapeDataString(EventId.value event.EventId)
    $"/api/v1/events/{source}/{eventId}"

let private mapIngestionResult (ctx: HttpContext) (event: ValidatedEvent) (result: IngestEventResult) =
    task {
        match result with
        | Success(Inserted _, _) ->
            ctx.Response.Headers.Location <- locationFor event
            return! writeSuccess ctx 201 "inserted"
        | Success(IdempotentReplay _, _) -> return! writeSuccess ctx 200 "idempotent_replay"
        | Success(EventIdentityConflict _, _)
        | Success(SequenceConflict _, _)
        | Success(CrossIdentityConflict _, _) -> return! writeError ctx 409 "event_conflict"
        | ContractViolation violations ->
            let status, code = violationStatus violations
            return! writeError ctx status code
        | AuthorizationFailure _ -> return! writeError ctx 403 "authorization_denied"
        | PersistenceFailure DatabaseUnavailable
        | PersistenceFailure SerializationRetriesExhausted -> return! writeError ctx 503 "service_unavailable"
        | PersistenceFailure _ -> return! writeError ctx 500 "internal_error"
    }

/// Production and tests use the same composed handler.  The handler invokes
/// the injected ports; it never constructs a data source or persistence
/// adapter and it authorizes before reading any request bytes.
let ingestEventHandler (authorize: AuthorizationPort) (ingest: IngestionPort) : HttpHandler =
    fun _next ctx ->
        task {
            let! authorization = authorize ctx

            match authorization with
            | Error _ -> return! writeError ctx 403 "authorization_denied"
            | Ok principal ->
                if not (supportedMediaType ctx.Request.ContentType) then
                    return! writeError ctx 415 "unsupported_content_type"
                else
                    let! body = readBoundedBody ctx

                    match body with
                    | None -> return! writeError ctx 413 "body_too_large"
                    | Some bytes ->
                        match EventDecoder.decode maxBodySize (ReadOnlyMemory bytes) with
                        | Error violations ->
                            let status, code = violationStatus violations
                            return! writeError ctx status code
                        | Ok event ->
                            match IngestionAuthorization.validateInstanceAuthorization principal event.InstanceId with
                            | Error _ -> return! writeError ctx 403 "authorization_denied"
                            | Ok() ->
                                let request =
                                    { Event = event
                                      RawBody = bytes
                                      EnvelopeJson = Encoding.UTF8.GetString bytes }

                                let! result = ingest request
                                return! mapIngestionResult ctx event result
        }
