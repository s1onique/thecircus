module Circus.Api.Http

open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.AspNetCore.Http
open Giraffe
open Circus.Domain

/// Liveness response payload.
type LivenessResponse = { Status: string }

/// Public product response DTO.
type ProductResponse =
    { [<JsonPropertyName("name")>]
      Name: string
      [<JsonPropertyName("tagline")>]
      Tagline: string
      [<JsonPropertyName("description")>]
      Description: string }

/// Project a domain ProductIdentity onto the public response DTO.
let toProductResponse (identity: ProductIdentity) : ProductResponse =
    { Name = ProductName.value identity.Name
      Tagline = ProductTagline.value identity.Tagline
      Description = ProductDescription.value identity.Description }

/// GET /health/live -> { "status": "live" }
let livenessHandler: HttpHandler =
    fun (_next: HttpFunc) (ctx: HttpContext) -> (json { Status = "live" }: HttpHandler) _next ctx

/// GET /api/v1/about -> product identity
let aboutHandler: HttpHandler =
    fun (_next: HttpFunc) (ctx: HttpContext) ->
        let response = ProductIdentity.current |> toProductResponse
        (json response: HttpHandler) _next ctx

/// Unknown API route -> bounded JSON 404.
let apiNotFoundHandler: HttpHandler =
    fun (_next: HttpFunc) (ctx: HttpContext) ->
        ctx.Response.StatusCode <- 404
        ctx.Response.ContentType <- "application/json"
        let body: obj = {| Error = "not_found" |}
        (json body: HttpHandler) _next ctx
