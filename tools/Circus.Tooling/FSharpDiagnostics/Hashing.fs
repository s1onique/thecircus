module Circus.Tooling.FSharpDiagnostics.Hashing

open System.Security.Cryptography
open System.Text

/// Lowercase SHA-256 hexadecimal of the supplied bytes.
let sha256Hex (bytes: byte[]) : string =
    use hash = SHA256.Create()
    let digest = hash.ComputeHash bytes
    let sb = StringBuilder(digest.Length * 2)
    for b in digest do
        sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)) |> ignore
    sb.ToString()

/// Lowercase SHA-256 hexadecimal of the file content. Reads deterministically
/// via a single byte buffer; the file is read once end-to-end.
let sha256OfFile (path: string) : string =
    use stream = System.IO.File.OpenRead(path)
    use hash = SHA256.Create()
    let mutable keepGoing = true
    let buffer = Array.zeroCreate<byte> 65536
    while keepGoing do
        let n = stream.Read(buffer, 0, buffer.Length)
        if n <= 0 then
            keepGoing <- false
        else
            hash.TransformBlock(buffer, 0, n, null, 0) |> ignore
    hash.TransformFinalBlock(Array.empty, 0, 0) |> ignore
    let digest = hash.Hash
    let sb = StringBuilder(digest.Length * 2)
    for b in digest do
        sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)) |> ignore
    sb.ToString()

/// Lowercase SHA-256 hexadecimal of a UTF-8 encoded string.
let sha256OfUtf8 (text: string) : string =
    sha256Hex (System.Text.Encoding.UTF8.GetBytes text)