module Circus.DevHost.Downloads

open System
open System.IO
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks

open Domain

/// HTTP semantics exposed by the download adapter. We fail closed on every
/// non-success status and never retry silently.
type DownloadResult = {
    Path: string
    Bytes: int64
    ContentType: string option
}

/// Verify the SHA-256 of an already-downloaded file. Used immediately
/// after the download completes and again each time we open the cache.
let verifyDownloaded (path: string) (expected: string) : Result<unit, DevHostFailure> =
    Integrity.verifyFile path expected Integrity.Sha256

/// Port for HTTP downloads. Tests provide a stub that responds without
/// touching the network.
type IHttp =
    abstract Download : uri:string * destPath:string * expectedSha256:string * ct:CancellationToken ->
        Async<Result<DownloadResult, DevHostFailure>>

/// Real HTTP adapter using `HttpClient`.
type RealHttp(timeout: TimeSpan) =

    let client =
        let handler = new SocketsHttpHandler()
        handler.UseCookies <- false
        handler.AutomaticDecompression <- DecompressionMethods.None
        let c = new HttpClient(handler)
        c.Timeout <- timeout
        c.DefaultRequestHeaders.UserAgent.ParseAdd("circus-dev/1.0")
        c

    interface IHttp with
        member _.Download(uri, destPath, expected, ct) =
            async {
                try
                    let stream = File.Create destPath
                    try
                        let url = Uri uri
                        let response =
                            client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                .GetAwaiter().GetResult()
                        if not response.IsSuccessStatusCode then
                            stream.Close()
                            try File.Delete destPath with _ -> ()
                            let code = int response.StatusCode
                            return Error(DownloadFailure(uri, sprintf "HTTP %d" code))
                        else
                            let contentStream =
                                response.Content.ReadAsStream()
                            contentStream.CopyToAsync(stream, 8192, ct)
                                .GetAwaiter().GetResult() |> ignore
                            stream.Flush()
                            stream.Close()
                            let len = (FileInfo destPath).Length
                            let ctype =
                                response.Content.Headers.ContentType
                                |> Option.ofObj
                                |> Option.map (fun h -> h.ToString())
                            match verifyDownloaded destPath expected with
                            | Ok _ -> return Ok({ Path = destPath; Bytes = len; ContentType = ctype })
                            | Error f ->
                                try File.Delete destPath with _ -> ()
                                return Error f
                    with
                    | :? AggregateException as agg ->
                        stream.Close()
                        try File.Delete destPath with _ -> ()
                        match agg.InnerException with
                        | null -> return Error(DownloadFailure(uri, agg.Message))
                        | ex -> return Error(DownloadFailure(uri, ex.Message))
                    | ex ->
                        stream.Close()
                        try File.Delete destPath with _ -> ()
                        if ex :? OperationCanceledException then
                            return Error(DownloadFailure(uri, "cancelled"))
                        else
                            return Error(DownloadFailure(uri, ex.Message))
                with ex ->
                    if ex :? OperationCanceledException then
                        return Error(DownloadFailure(uri, "cancelled"))
                    elif ex :? HttpRequestException then
                        return Error(DownloadFailure(uri, ex.Message))
                    else
                        return Error(DownloadFailure(uri, ex.Message))
            }

/// Atomic cache placement: `dest` is moved into `cacheDir` via a temp file
/// then rename. If the destination already passes integrity we keep it;
/// otherwise we delete it and move the new file in.
let cachePlace
    (cacheDir: string)
    (finalName: string)
    (expectedSha256: string)
    (source: string)
    : Result<string, DevHostFailure> =
    try
        if not (Directory.Exists cacheDir) then
            Directory.CreateDirectory cacheDir |> ignore
        let target = Path.Combine(cacheDir, finalName)
        // If the existing cache is valid we are happy with it.
        if File.Exists target then
            match verifyDownloaded target expectedSha256 with
            | Ok _ -> Ok target
            | Error _ ->
                File.Delete target
                File.Move(source, target)
                Ok target
        else
            File.Move(source, target)
            Ok target
    with ex ->
        Error(ExtractionFailure(source, ex.Message))

/// A cached entry with cached integral verification.
type CachedEntry = {
    Path: string
    Verified: bool
}

let loadCachedEntry
    (cacheDir: string)
    (finalName: string)
    (expectedSha256: string)
    : CachedEntry option =
    let path = Path.Combine(cacheDir, finalName)
    if not (File.Exists path) then None
    else
        match verifyDownloaded path expectedSha256 with
        | Ok _ -> Some { Path = path; Verified = true }
        | Error _ ->
            try File.Delete path with _ -> ()
            None

/// Decision for `downloadIfMissing` used by downloader installers.
type DownloadDecision =
    | UseCached of string
    | DownloadFresh

let classifyDownload
    (cacheDir: string)
    (finalName: string)
    (expectedSha256: string)
    : DownloadDecision =
    match loadCachedEntry cacheDir finalName expectedSha256 with
    | Some entry -> UseCached entry.Path
    | None -> DownloadFresh
