module Circus.DevHost.Integrity

open System
open System.IO
open System.Security.Cryptography
open System.Text

open Domain

/// Pure helper to compute the SHA-256 of a string. Used by tests.
let sha256OfString (text: string) : string =
    use ms = new MemoryStream(Encoding.UTF8.GetBytes text)
    use hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
    let buf = Array.zeroCreate<byte> 8192
    let mutable read = ms.Read(buf, 0, buf.Length)
    while read > 0 do
        hasher.AppendData(buf, 0, read) |> ignore
        read <- ms.Read(buf, 0, buf.Length)
    BitConverter.ToString(hasher.GetHashAndReset()).Replace("-", "").ToLowerInvariant()

/// Compute SHA-256 of a file by streaming through it.
let sha256OfFile (path: string) : Result<string, DevHostFailure> =
    try
        if not (File.Exists path) then Error(DownloadFailure(path, "file missing"))
        else
            use fs = File.OpenRead path
            use hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            let buf = Array.zeroCreate<byte> 8192
            let mutable read = fs.Read(buf, 0, buf.Length)
            while read > 0 do
                hasher.AppendData(buf, 0, read) |> ignore
                read <- fs.Read(buf, 0, buf.Length)
            Ok(BitConverter.ToString(hasher.GetHashAndReset()).Replace("-", "").ToLowerInvariant())
    with ex ->
        Error(DownloadFailure(path, ex.Message))

/// Compute SHA-512 of a file. Used by tests of the engine path.
let sha512OfFile (path: string) : Result<string, DevHostFailure> =
    try
        if not (File.Exists path) then Error(DownloadFailure(path, "file missing"))
        else
            use fs = File.OpenRead path
            use hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA512)
            let buf = Array.zeroCreate<byte> 8192
            let mutable read = fs.Read(buf, 0, buf.Length)
            while read > 0 do
                hasher.AppendData(buf, 0, read) |> ignore
                read <- fs.Read(buf, 0, buf.Length)
            Ok(BitConverter.ToString(hasher.GetHashAndReset()).Replace("-", "").ToLowerInvariant())
    with ex ->
        Error(DownloadFailure(path, ex.Message))

/// Constant-time comparison of two hex string hashes. We rely on the
/// `CryptographicOperations.FixedTimeEquals` overload for byte arrays.
let constantTimeEqualHex (a: string) (b: string) : bool =
    if String.length a <> String.length b then false
    else
        let bytesA = Encoding.ASCII.GetBytes a
        let bytesB = Encoding.ASCII.GetBytes b
        CryptographicOperations.FixedTimeEquals(bytesA, bytesB)

/// Verify a file matches an expected hash with a clear semantic return value.
let verifyFile
    (path: string)
    (expected: string)
    (algorithm: IntegrityAlgorithm)
    : Result<unit, DevHostFailure> =
    let actual =
        match algorithm with
        | Sha256 -> sha256OfFile path
        | Sha512 -> sha512OfFile path
    match actual with
    | Error f -> Error f
    | Ok h ->
        if constantTimeEqualHex (h.ToLowerInvariant()) (expected.ToLowerInvariant()) then Ok ()
        else Error(IntegrityFailure(path, expected, h))

/// Verify a string (used for the bytes-on-the-wire form).
let verifyString (text: string) (expected: string) : bool =
    let actual = sha256OfString text
    constantTimeEqualHex (actual.ToLowerInvariant()) (expected.ToLowerInvariant())
