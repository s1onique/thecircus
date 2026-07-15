module Circus.Api.Tests.HttpContractTests

open System
open System.Net
open System.Net.Http
open System.Net
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Circus.Api.IngestionHandlers
open Circus.Api.Program
open Circus.Application
open Circus.Domain

#nowarn "0044"

/// Deterministic fake ports used by every API decision-table test.
type FakeState =
    { mutable Authorization: Result<ProducerPrincipal, IngestionAuthorizationFailure>
      mutable Ingestion: IngestEventResult
      mutable AuthorizationCalls: int
      mutable IngestionCalls: int }

let newState () =
    { Authorization =
        Ok
            { ProducerId = "test-producer"
              AllowedInstance = None }
      Ingestion = Success(Inserted(JournalPosition 1L), None)
      AuthorizationCalls = 0
      IngestionCalls = 0 }

let private createServer (state: FakeState) : TestServer =
    let authorize: AuthorizationPort =
        fun _ ->
            state.AuthorizationCalls <- state.AuthorizationCalls + 1
            Task.FromResult state.Authorization

    let ingest: IngestionPort =
        fun _ ->
            state.IngestionCalls <- state.IngestionCalls + 1
            Task.FromResult state.Ingestion

    let configureServices (services: IServiceCollection) = services.AddGiraffe() |> ignore

    let configureApp (app: IApplicationBuilder) =
        app.UseGiraffe(webApp authorize ingest) |> ignore

    let builder =
        WebHostBuilder().UseTestServer().ConfigureServices(configureServices).Configure(configureApp)

    new TestServer(builder)

let private withClient (state: FakeState) (f: HttpClient -> 'a) : 'a =
    use server = createServer state
    use client = server.CreateClient()
    f client

let private validBody (eventId: string) (source: string) (runId: Guid) =
    sprintf
        """{"specversion":"1.0","id":"%s","source":"%s","type":"io.leamas.execution.started.v1","subject":"run/%O","time":"2026-07-15T12:00:00Z","datacontenttype":"application/json","circusinstance":"builder-01","circusepoch":"%O","circusseq":1,"runid":"%O","data":{"repository_ref":"repo","leamas_version":"1.0.0"}}"""
        eventId
        source
        runId
        (Guid.NewGuid())
        runId

let private sendPost (client: HttpClient) (body: string) (mediaType: string option) =
    async {
        use request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        use content = new StringContent(body, Encoding.UTF8)
        content.Headers.Remove("Content-Type") |> ignore

        match mediaType with
        | Some value -> content.Headers.TryAddWithoutValidation("Content-Type", value) |> ignore
        | None -> ()

        request.Content <- content
        let! response = client.SendAsync(request) |> Async.AwaitTask
        let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        return response, responseBody
    }
    |> Async.RunSynchronously

type private ChunkedContent(bytes: byte[]) =
    inherit HttpContent()

    override _.TryComputeLength(length: byref<int64>) =
        length <- 0L
        false

    override _.SerializeToStreamAsync(stream: IO.Stream, _context: TransportContext) =
        stream.WriteAsync(bytes, 0, bytes.Length)

let private sendChunkedPost (client: HttpClient) (body: string) =
    async {
        use request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        use content = new ChunkedContent(Encoding.UTF8.GetBytes body)
        content.Headers.ContentType <- new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        request.Content <- content
        let! response = client.SendAsync(request) |> Async.AwaitTask
        let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        return response, responseBody
    }
    |> Async.RunSynchronously

let private jsonCode (body: string) =
    JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()

let tests =
    testList
        "HTTP Contracts"
        [ test "/health/live returns 200" {
              let state = newState ()

              withClient state (fun client ->
                  let response =
                      client.GetAsync("/health/live") |> Async.AwaitTask |> Async.RunSynchronously

                  Expect.equal response.StatusCode HttpStatusCode.OK "Should return 200")
          }

          test "/api/v1/about remains a stable public response" {
              let state = newState ()

              withClient state (fun client ->
                  let response =
                      client.GetAsync("/api/v1/about") |> Async.AwaitTask |> Async.RunSynchronously

                  let body =
                      response.Content.ReadAsStringAsync()
                      |> Async.AwaitTask
                      |> Async.RunSynchronously

                  let doc = JsonDocument.Parse(body)
                  Expect.equal response.StatusCode HttpStatusCode.OK "Should return 200"
                  Expect.equal (doc.RootElement.GetProperty("name").GetString()) "The Circus" "Domain name")
          }

          test "authorization denial is 403 and short-circuits body and ingestion" {
              let state = newState ()
              state.Authorization <- Error MissingCredentials

              withClient state (fun client ->
                  let response, _ =
                      sendPost client "not JSON and deliberately not parsed" (Some "application/json")

                  Expect.equal response.StatusCode HttpStatusCode.Forbidden "Denied requests are forbidden"
                  Expect.equal state.IngestionCalls 0 "Ingestion must not be called")
          }

          test "authorization adapter failures are also 403" {
              let state = newState ()
              state.Authorization <- Error InvalidCredentials

              withClient state (fun client ->
                  let response, body =
                      sendPost client (validBody "id-1" "urn:test" (Guid.NewGuid())) (Some "application/json")

                  Expect.equal response.StatusCode HttpStatusCode.Forbidden "Authorization failures are generic"
                  Expect.equal (jsonCode body) "authorization_denied" "Stable code"
                  Expect.equal state.IngestionCalls 0 "Ingestion must not be called")
          }

          test "missing content type is 415" {
              let state = newState ()

              withClient state (fun client ->
                  let response, _ =
                      sendPost client (validBody "id-2" "urn:test" (Guid.NewGuid())) None

                  Expect.equal response.StatusCode HttpStatusCode.UnsupportedMediaType "Missing type is rejected"
                  Expect.equal state.IngestionCalls 0 "Ingestion must not be called")
          }

          test "malformed content type is 415" {
              let state = newState ()

              withClient state (fun client ->
                  let response, _ =
                      sendPost client (validBody "id-3" "urn:test" (Guid.NewGuid())) (Some "application/json; =bad")

                  Expect.equal response.StatusCode HttpStatusCode.UnsupportedMediaType "Malformed type is rejected"
                  Expect.equal state.IngestionCalls 0 "Ingestion must not be called")
          }

          test "application/json with parameters is accepted" {
              let state = newState ()
              let runId = Guid.NewGuid()

              withClient state (fun client ->
                  let response, _ =
                      sendPost client (validBody "id-4" "urn:test" runId) (Some "Application/JSON; charset=utf-8")

                  Expect.equal response.StatusCode HttpStatusCode.Created "JSON is accepted"
                  Expect.equal state.IngestionCalls 1 "Ingestion called once")
          }

          test "application vendor plus json is accepted" {
              let state = newState ()

              withClient state (fun client ->
                  let response, _ =
                      sendPost
                          client
                          (validBody "id-5" "urn:test" (Guid.NewGuid()))
                          (Some "application/cloudevents+json")

                  Expect.equal response.StatusCode HttpStatusCode.Created "Vendor JSON is accepted")
          }

          test "malformed JSON is 400 and does not ingest" {
              let state = newState ()

              withClient state (fun client ->
                  let response, body = sendPost client "{" (Some "application/json")
                  Expect.equal response.StatusCode HttpStatusCode.BadRequest "Malformed JSON is bad request"
                  Expect.equal (jsonCode body) "malformed_json" "Stable malformed code"
                  Expect.equal state.IngestionCalls 0 "No ingestion")
          }

          test "duplicate top-level JSON property is 400" {
              let state = newState ()
              let runId = Guid.NewGuid()
              let body = validBody "id-6" "urn:test" runId + "\n"
              let duplicate = body.Replace("}\n", ",\"id\":\"second\"}\n")

              withClient state (fun client ->
                  let response, _ = sendPost client duplicate (Some "application/json")
                  Expect.equal response.StatusCode HttpStatusCode.BadRequest "Duplicate top-level property"
                  Expect.equal state.IngestionCalls 0 "No ingestion")
          }

          test "duplicate nested JSON property is 400" {
              let state = newState ()
              let runId = Guid.NewGuid()
              let body = validBody "id-7" "urn:test" runId

              let duplicate =
                  body.Replace(
                      "\"leamas_version\":\"1.0.0\"",
                      "\"leamas_version\":\"1.0.0\",\"leamas_version\":\"2.0.0\""
                  )

              withClient state (fun client ->
                  let response, _ = sendPost client duplicate (Some "application/json")
                  Expect.equal response.StatusCode HttpStatusCode.BadRequest "Duplicate nested property"
                  Expect.equal state.IngestionCalls 0 "No ingestion")
          }

          test "valid JSON violating the event contract is 422" {
              let state = newState ()
              let runId = Guid.NewGuid()

              let body =
                  (validBody "id-8" "urn:test" runId).Replace("\"repository_ref\":\"repo\"", "\"repository_ref\":\"\"")

              withClient state (fun client ->
                  let response, body = sendPost client body (Some "application/json")
                  Expect.equal response.StatusCode (enum<HttpStatusCode> 422) "Contract violations are 422"
                  Expect.equal (jsonCode body) "contract_violation" "Stable validation code"
                  Expect.equal state.IngestionCalls 0 "No ingestion")
          }

          test "declared oversized body is 413" {
              let state = newState ()
              let body = String('x', requestBodyLimit + 1)

              withClient state (fun client ->
                  let response, _ = sendPost client body (Some "application/json")
                  Expect.equal response.StatusCode (enum<HttpStatusCode> 413) "Oversized body"
                  Expect.equal state.IngestionCalls 0 "No ingestion")
          }

          test "streamed oversized body is bounded and 413" {
              let state = newState ()
              let body = String('x', requestBodyLimit + 1)

              withClient state (fun client ->
                  let response, _ = sendChunkedPost client body
                  Expect.equal response.StatusCode (enum<HttpStatusCode> 413) "Streamed oversized body"
                  Expect.equal state.IngestionCalls 0 "No ingestion")
          }

          test "inserted event is 201 with safe relative Location" {
              let state = newState ()
              let runId = Guid.NewGuid()

              withClient state (fun client ->
                  let response, body =
                      sendPost
                          client
                          (validBody "id/with?unsafe#chars" "urn:source/with?chars" runId)
                          (Some "application/json")

                  let location = response.Headers.Location.OriginalString
                  Expect.equal response.StatusCode HttpStatusCode.Created "Inserted"
                  let result = JsonDocument.Parse(body).RootElement.GetProperty("result").GetString()
                  Expect.equal result "inserted" "Success code"
                  Expect.isTrue (location.StartsWith("/api/v1/events/")) "Relative location"
                  Expect.isTrue (location.Contains("%2F") || location.Contains("%2f")) "Slash is escaped"
                  Expect.isFalse (location.Contains("?chars")) "Query is not injected"
                  Expect.isFalse (body.Contains("raw_body")) "No persistence details")
          }

          test "idempotent replay is 200" {
              let state = newState ()
              state.Ingestion <- Success(IdempotentReplay(JournalPosition 1L), None)

              withClient state (fun client ->
                  let response, _ =
                      sendPost client (validBody "id-9" "urn:test" (Guid.NewGuid())) (Some "application/json")

                  Expect.equal response.StatusCode HttpStatusCode.OK "Replay status")
          }

          test "conflict is 409" {
              let state = newState ()
              state.Ingestion <- Success(SequenceConflict(JournalPosition 1L), None)

              withClient state (fun client ->
                  let response, body =
                      sendPost client (validBody "id-10" "urn:test" (Guid.NewGuid())) (Some "application/json")

                  Expect.equal response.StatusCode HttpStatusCode.Conflict "Conflict status"
                  Expect.equal (jsonCode body) "event_conflict" "Stable conflict code")
          }

          test "retry exhaustion is 503 with generic body" {
              let state = newState ()
              state.Ingestion <- PersistenceFailure SerializationRetriesExhausted

              withClient state (fun client ->
                  let response, body =
                      sendPost client (validBody "id-11" "urn:test" (Guid.NewGuid())) (Some "application/json")

                  Expect.equal response.StatusCode HttpStatusCode.ServiceUnavailable "Unavailable"
                  Expect.equal (jsonCode body) "service_unavailable" "Generic code"

                  Expect.isFalse
                      (body.Contains("40001") || body.Contains("circus_event_journal"))
                      "No persistence details")
          }

          test "invariant and unexpected persistence failures are generic 500" {
              let state = newState ()
              state.Ingestion <- PersistenceFailure ProjectionInvariantFailed

              withClient state (fun client ->
                  let response, body =
                      sendPost client (validBody "id-12" "urn:test" (Guid.NewGuid())) (Some "application/json")

                  Expect.equal response.StatusCode HttpStatusCode.InternalServerError "Internal failure"
                  Expect.equal (jsonCode body) "internal_error" "Generic code"
                  Expect.isFalse (body.Contains("ProjectionInvariantFailed")) "No union case leak")
          } ]
