module Circus.Api.Program

open System
open System.IO
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.FileProviders
open Microsoft.Extensions.Hosting
open Giraffe
open Npgsql
open Circus.Api.Http
open Circus.Api.IngestionHandlers
open Circus.Persistence.Postgres

let webDistPath () : string =
    let candidates =
        [ Path.Combine(AppContext.BaseDirectory, "web", "dist")
          Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "web", "dist")
          Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "web", "dist")
          Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "web", "dist") ]

    candidates
    |> List.tryFind (fun p -> File.Exists(Path.Combine(p, "index.html")))
    |> Option.defaultValue (List.head candidates)

let indexHtmlAvailable () =
    File.Exists(Path.Combine(webDistPath (), "index.html"))

/// Read and validate the mandatory production setting.  No fallback user,
/// password, host, or database is ever supplied by the host.
let postgresConnectionString () : string =
    let value = Environment.GetEnvironmentVariable("CIRCUS_DATABASE_URL")

    if String.IsNullOrWhiteSpace value then
        failwith "CIRCUS_DATABASE_URL is required and must be a valid PostgreSQL connection string"

    try
        let parsed = NpgsqlConnectionStringBuilder(value)

        if
            String.IsNullOrWhiteSpace parsed.Host
            || String.IsNullOrWhiteSpace parsed.Database
        then
            failwith "CIRCUS_DATABASE_URL is required and must be a valid PostgreSQL connection string"

        value
    with :? ArgumentException ->
        failwith "CIRCUS_DATABASE_URL is required and must be a valid PostgreSQL connection string"

/// Compose the route from ports.  This function is the API test seam: it does
/// not know how either port is implemented.
let webApp (authorize: AuthorizationPort) (ingest: IngestionPort) : HttpHandler =
    choose
        [ subRoute "/health" (choose [ GET >=> route "/live" >=> livenessHandler ])
          subRoute
              "/api/v1"
              (choose
                  [ GET >=> route "/about" >=> aboutHandler
                    POST >=> route "/events" >=> ingestEventHandler authorize ingest
                    apiNotFoundHandler ])
          GET
          >=> route "/"
          >=> fun _next ctx ->
              task {
                  if indexHtmlAvailable () then
                      let filePath = Path.Combine(webDistPath (), "index.html")
                      ctx.Response.ContentType <- "text/html; charset=utf-8"
                      let! bytes = File.ReadAllBytesAsync(filePath)
                      do! ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length)
                      return Some ctx
                  else
                      ctx.Response.StatusCode <- 503
                      ctx.Response.ContentType <- "text/plain; charset=utf-8"
                      do! ctx.Response.WriteAsync("Frontend not built. Run 'make build-web' first.")
                      return Some ctx
              } ]

let configureServices (dataSource: NpgsqlDataSource) (services: IServiceCollection) : unit =
    services.AddGiraffe() |> ignore
    // The host creates exactly one data source and the provider owns its
    // disposal.  No request or retry path constructs another pool.
    services.AddSingleton<NpgsqlDataSource>(dataSource) |> ignore

let staticFileOptions () : StaticFileOptions =
    new StaticFileOptions(FileProvider = new PhysicalFileProvider(webDistPath ()), ServeUnknownFileTypes = true)

let configureApp (app: IApplicationBuilder) : unit =
    if Directory.Exists(webDistPath ()) then
        app.UseStaticFiles(staticFileOptions ()) |> ignore

    let dataSource = app.ApplicationServices.GetRequiredService<NpgsqlDataSource>()
    let service = IngestEventService.create dataSource
    app.UseGiraffe(webApp AuthorizationAdapters.denyAll service.Ingest) |> ignore

/// Build the production host.  CIRCUS_DATABASE_URL is validated before the
/// host is built, and its single data source is registered as a singleton.
let buildHost (args: string[]) : IHost =
    let connectionString = postgresConnectionString ()

    let dataSource =
        PostgresConfiguration.createDataSource (PostgresConfiguration.defaultConfiguration connectionString)

    let kestrelOptions (options: KestrelServerOptions) : unit = options.ListenLocalhost(5000)

    let configureWebHost (builder: IWebHostBuilder) : unit =
        builder
            .ConfigureKestrel(kestrelOptions)
            .ConfigureServices(fun services -> configureServices dataSource services)
            .Configure(configureApp)
        |> ignore

    Host.CreateDefaultBuilder(args).ConfigureWebHostDefaults(configureWebHost).Build()

[<EntryPoint>]
let main args =
    (buildHost args).Run()
    0
