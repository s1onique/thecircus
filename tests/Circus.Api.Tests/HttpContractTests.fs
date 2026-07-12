module Circus.Api.Tests.HttpContractTests

open System.Net
open System.Net.Http
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open Circus.Api.Program
open Circus.Api.Http

// The WebHostBuilder/UseTestServer pair is the documented test path for
// non-MVC Giraffe applications; the newer WebApplicationFactory cannot be
// used here because our application is built through ConfigureWebHostDefaults
// rather than WebApplication.CreateBuilder. The deprecation is therefore
// unavoidable for this ACT and is restricted to this test module.
#nowarn "0044"

/// Configure services for the in-process test host.
let configureTestServices (services: IServiceCollection) : unit = services.AddGiraffe() |> ignore

/// Configure the test pipeline. Only Giraffe routing is wired; static files
/// are skipped because the Elm artefact is not built during the test run.
let configureTestApp (app: IApplicationBuilder) : unit = app.UseGiraffe webApp |> ignore

/// Build an in-process TestServer that requires no listening TCP port.
let createServer () : TestServer =
    let builder =
        WebHostBuilder().UseTestServer().ConfigureServices(configureTestServices).Configure(configureTestApp)

    new TestServer(builder)

/// Acquire a TestServer and an HttpClient from it, returning disposable
/// wrappers so callers can write `use _ = withClient f`.
let withClient (f: HttpClient -> 'a) : 'a =
    let server = createServer ()
    let client = server.CreateClient()

    try
        f client
    finally
        client.Dispose()
        (server :> System.IDisposable).Dispose()

let readJsonPropertyNames (json: string) : Set<string> =
    let doc = JsonDocument.Parse(json)
    doc.RootElement.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Set.ofSeq

let get (client: HttpClient) (path: string) =
    async {
        let req = new HttpRequestMessage(HttpMethod.Get, path)
        let! response = client.SendAsync(req) |> Async.AwaitTask
        let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        return response, body
    }
    |> Async.RunSynchronously

let tests =
    testList
        "HTTP Contracts"
        [ test "/health/live returns 200" {
              withClient (fun client ->
                  let response, _ = get client "/health/live"
                  Expect.equal response.StatusCode HttpStatusCode.OK "Should return 200")
          }

          test "/health/live content type is JSON" {
              withClient (fun client ->
                  let response, _ = get client "/health/live"
                  let contentType = response.Content.Headers.ContentType.MediaType
                  Expect.equal contentType "application/json" "Content type should be JSON")
          }

          test "/health/live body has exactly { status }" {
              withClient (fun client ->
                  let _, body = get client "/health/live"

                  Expect.equal
                      (readJsonPropertyNames body)
                      (Set.singleton "status")
                      "Liveness body should have exactly one 'status' field")
          }

          test "/health/live status field is 'live'" {
              withClient (fun client ->
                  let _, body = get client "/health/live"
                  let doc = JsonDocument.Parse(body)
                  let status = doc.RootElement.GetProperty("status").GetString()
                  Expect.equal status "live" "Status should be 'live'")
          }

          test "/api/v1/about returns 200" {
              withClient (fun client ->
                  let response, _ = get client "/api/v1/about"
                  Expect.equal response.StatusCode HttpStatusCode.OK "Should return 200")
          }

          test "/api/v1/about contains domain values" {
              withClient (fun client ->
                  let _, body = get client "/api/v1/about"
                  let doc = JsonDocument.Parse(body)

                  Expect.equal
                      (doc.RootElement.GetProperty("name").GetString())
                      "The Circus"
                      "Name should match domain"

                  Expect.equal
                      (doc.RootElement.GetProperty("tagline").GetString())
                      "Team-scale Leamas"
                      "Tagline should match domain"

                  Expect.equal
                      (doc.RootElement.GetProperty("description").GetString())
                      "The team-scale coordination, evidence, and governance platform for Leamas."
                      "Description should match domain")
          }

          test "/api/v1/about has exactly the documented fields" {
              withClient (fun client ->
                  let _, body = get client "/api/v1/about"

                  Expect.equal
                      (readJsonPropertyNames body)
                      (Set.ofList [ "name"; "tagline"; "description" ])
                      "About body should have exactly name, tagline, description")
          }

          test "unknown /api/v1/* route returns 404" {
              withClient (fun client ->
                  let response, _ = get client "/api/v1/unknown"
                  Expect.equal response.StatusCode HttpStatusCode.NotFound "Should return 404")
          }

          test "unknown API response is JSON with error field" {
              withClient (fun client ->
                  let response, body = get client "/api/v1/unknown"
                  let contentType = response.Content.Headers.ContentType.MediaType
                  Expect.equal contentType "application/json" "Unknown API response should be JSON"
                  let doc = JsonDocument.Parse(body)

                  Expect.equal
                      (doc.RootElement.GetProperty("error").GetString())
                      "not_found"
                      "Error field should be 'not_found'")
          }

          test "no development exception details appear in unknown response" {
              withClient (fun client ->
                  let _, body = get client "/api/v1/unknown"

                  Expect.isFalse
                      (body.Contains("Exception") || body.Contains("at Circus"))
                      "Response should not contain exception or stack-trace details")
          } ]
