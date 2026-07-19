module Circus.DevHost.Downloads

open System
open System.IO
open System.Net
open System.Net.Http
open System.Threading

open Domain

/// Integrity contract for a downloaded payload. Metadata that has no
/// independently published payload hash must say so explicitly rather than
/// using an empty-string sentinel.
type ExpectedIntegrity =
    | NoPayloadHash
    | Sha256 of string
    | Sha512 of string

type DownloadResult =
    { Path: string
      Bytes: int64
      ContentType: string option }

let verifyDownloaded (path: string) (expected: ExpectedIntegrity) : Result<unit, DevHostFailure> =
    match expected with
    | NoPayloadHash ->
        if File.Exists path then
            Ok()
        else
            Error(DownloadFailure(path, "file missing"))
    | Sha256 hash -> Integrity.verifyFile path hash IntegrityAlgorithm.Sha256
    | Sha512 hash -> Integrity.verifyFile path hash IntegrityAlgorithm.Sha512

type IHttp =
    abstract Download:
        uri: string * destPath: string * expectedIntegrity: ExpectedIntegrity * ct: CancellationToken ->
            Async<Result<DownloadResult, DevHostFailure>>

type RealHttp(timeout: TimeSpan) =
    let client =
        let handler = new SocketsHttpHandler()
        handler.UseCookies <- false
        handler.AutomaticDecompression <- DecompressionMethods.None
        let httpClient = new HttpClient(handler)
        httpClient.Timeout <- timeout
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd "circus-dev/1.0"
        httpClient

    interface IHttp with
        member _.Download(uri, destPath, expected, cancellation) =
            async {
                let deletePartial () =
                    try
                        if File.Exists destPath then
                            File.Delete destPath
                    with _ ->
                        ()

                try
                    use response =
                        client
                            .GetAsync(Uri uri, HttpCompletionOption.ResponseHeadersRead, cancellation)
                            .GetAwaiter()
                            .GetResult()

                    if not response.IsSuccessStatusCode then
                        deletePartial ()
                        return Error(DownloadFailure(uri, sprintf "HTTP %d" (int response.StatusCode)))
                    else
                        let bytes =
                            use source = response.Content.ReadAsStream(cancellation)
                            use destination = File.Create destPath
                            source.CopyToAsync(destination, 8192, cancellation).GetAwaiter().GetResult()
                            destination.Flush true
                            destination.Length

                        let contentTypeHeader = response.Content.Headers.ContentType

                        let contentType =
                            match contentTypeHeader with
                            | null -> None
                            | header -> Some(header.ToString())

                        match verifyDownloaded destPath expected with
                        | Ok() ->
                            return
                                Ok
                                    { Path = destPath
                                      Bytes = bytes
                                      ContentType = contentType }
                        | Error failure ->
                            deletePartial ()
                            return Error failure
                with
                | :? OperationCanceledException ->
                    deletePartial ()
                    return Error(DownloadFailure(uri, "cancelled"))
                | ex ->
                    deletePartial ()
                    return Error(DownloadFailure(uri, ex.Message))
            }

let cachePlace
    (cacheDir: string)
    (finalName: string)
    (expected: ExpectedIntegrity)
    (source: string)
    : Result<string, DevHostFailure> =
    try
        Directory.CreateDirectory cacheDir |> ignore
        let target = Path.Combine(cacheDir, finalName)

        if File.Exists target then
            match verifyDownloaded target expected with
            | Ok() -> Ok target
            | Error _ ->
                File.Delete target
                File.Move(source, target)
                Ok target
        else
            File.Move(source, target)
            Ok target
    with ex ->
        Error(ExtractionFailure(source, ex.Message))

type CachedEntry = { Path: string; Verified: bool }

let loadCachedEntry (cacheDir: string) (finalName: string) (expected: ExpectedIntegrity) : CachedEntry option =
    let path = Path.Combine(cacheDir, finalName)

    if not (File.Exists path) then
        None
    else
        match verifyDownloaded path expected with
        | Ok() -> Some { Path = path; Verified = true }
        | Error _ ->
            try
                File.Delete path
            with _ ->
                ()

            None

type DownloadDecision =
    | UseCached of string
    | DownloadFresh

let classifyDownload (cacheDir: string) (finalName: string) (expected: ExpectedIntegrity) : DownloadDecision =
    match loadCachedEntry cacheDir finalName expected with
    | Some entry -> UseCached entry.Path
    | None -> DownloadFresh
