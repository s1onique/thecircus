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
open Circus.Api.Http
open Circus.Persistence.Postgres

/// Path of the generated Elm assets directory. Walks a small number of fixed
/// steps up from the assembly directory; the ASP.NET build copies the
/// artefacts into `web/dist` next to the project output. The lookup is
/// purely filesystem-based, so the gate never depends on a developer's
/// machine-specific absolute path.
let webDistPath () : string =
    let candidates =
        [ Path.Combine(AppContext.BaseDirectory, "web", "dist")
          Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "web", "dist")
          Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "web", "dist")
          Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "web", "dist") ]

    candidates
    |> List.tryFind (fun p -> File.Exists(Path.Combine(p, "index.html")))
    |> Option.defaultValue (List.head candidates)

/// True when the generated Elm application root asset is present.
let indexHtmlAvailable () : bool =
    File.Exists(Path.Combine(webDistPath (), "index.html"))

/// Get PostgreSQL connection string from environment or default.
let postgresConnectionString () : string =
    match Environment.GetEnvironmentVariable("CIRCUS_DATABASE_URL") with
    | null -> "Host=localhost;Database=circus;Username=postgres;Password=postgres"
    | url -> url

/// Composite Giraffe web application.
let webApp (dataSource: Npgsql.NpgsqlDataSource) : HttpHandler =
    choose
        [ subRoute "/health" (choose [ GET >=> route "/live" >=> livenessHandler ])
          subRoute "/api/v1" (
              choose [ GET >=> route "/about" >=> aboutHandler
                       POST >=> route "/events" >=> IngestionHandlers.ingestEventHandler dataSource
                       apiNotFoundHandler ]
          )
          GET
          >=> route "/"
          >=> fun (_next: HttpFunc) (ctx: HttpContext) ->
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

/// Configure application services. The plain .NET delegate overload is used
/// because Giraffe registers itself through standard DI extensions.
let configureServices (services: IServiceCollection) : unit =
    services.AddGiraffe() |> ignore

/// Build the static-file options pointing at the generated Elm assets.
let staticFileOptions () : StaticFileOptions =
    new StaticFileOptions(FileProvider = new PhysicalFileProvider(webDistPath ()), ServeUnknownFileTypes = true)

/// Configure the application pipeline.
let configureApp (app: IApplicationBuilder) (dataSource: Npgsql.NpgsqlDataSource) : unit =
    if Directory.Exists(webDistPath ()) then
        app.UseStaticFiles(staticFileOptions ()) |> ignore

    app.UseGiraffe(webApp dataSource) |> ignore

/// Build a web host. The same configuration path is used by both production
/// startup and integration tests, but tests override the host with
/// `UseTestServer` so no real TCP port is opened.
let buildHost (args: string[]) : IHost =
    let connectionString = postgresConnectionString ()
    let config = PostgresConfiguration.defaultConfiguration connectionString
    let dataSource = PostgresConfiguration.createDataSource config

    let kestrelOptions (options: KestrelServerOptions) : unit = options.ListenLocalhost(5000)

    let configureWebHost (builder: IWebHostBuilder) : unit =
        let step1 = builder.ConfigureKestrel(kestrelOptions)
        let step2 = step1.ConfigureServices(configureServices)
        step2.Configure(fun app -> configureApp app dataSource) |> ignore

    Host.CreateDefaultBuilder(args).ConfigureWebHostDefaults(configureWebHost).Build()

[<EntryPoint>]
let main args =
    (buildHost args).Run()
    0
