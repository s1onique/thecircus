module Circus.Tooling.SourcePolicy.NulInventory

/// Pure NUL-delimited record parser.
///
/// ``git ls-files -z`` emits filenames as opaque, non-NUL byte
/// sequences terminated by NUL.  The parser is pure over bytes: it
/// never touches the file system, never spawns a process, and never
/// depends on character encoding until the final UTF-8 decode step.
///
/// The UTF-8 decoder uses throw-on-invalid semantics so a single
/// invalid byte surfaces as a deterministic diagnostic instead of
/// being silently replaced with the Unicode replacement character.

open System
open System.Text

type DecodeFailure =
    | InvalidUtf8 of badByte: byte * byteOffset: int
    | UnterminatedFinalRecord of expected: int
    | EmptyInteriorRecord of recordIndex: int

type DecodeDiagnostic = {
    CommandId: string
    RecordIndex: int
    ByteOffset: int
    Category: DecodeFailure
    SafeBytesHex: string
}

type ParseResult =
    | Ok of paths: string list
    | Error of diagnostic: DecodeDiagnostic

let private hexBytes (bytes: byte[]) : string =
    let sb = Text.StringBuilder()
    for b in bytes do
        if sb.Length > 0 then sb.Append(' ') |> ignore
        sb.AppendFormat("{0:x2}", b) |> ignore
    sb.ToString()

let private sanitiseAscii (s: string) : string =
    let sb = Text.StringBuilder()
    for c in s do
        let i = int c
        if (i >= 0x20 && i <= 0x7E) || c = ' ' then
            sb.Append c |> ignore
    sb.ToString()

/// Strict UTF-8 decoder that throws on the first invalid byte.
/// Returns ``Some (badByte, offset)`` on failure, ``None`` when the
/// buffer decodes cleanly.  Throws are caught and translated to
/// diagnostic-friendly values by the caller.
let private decodeStrict (recordBytes: byte[]) (recordIndex: int) (commandId: string) : Result<string, DecodeDiagnostic> =
    let strict = UTF8Encoding(false, true)
    try
        let s = strict.GetString(recordBytes)
        if s = "" then
            // Defensive: GetString on empty returns empty.  Treat as
            // empty interior record.
            Result.Error
                { CommandId = commandId
                  RecordIndex = recordIndex
                  ByteOffset = 0
                  Category = EmptyInteriorRecord recordIndex
                  SafeBytesHex = "" }
        else
            Result.Ok s
    with
    | :? DecoderFallbackException as ex ->
        // The strict encoder has already substituted a fallback char.
        // We surface this as a generic invalid-byte diagnostic using
        // the first byte of the record as the offending byte.
        let badByte =
            if recordBytes.Length > 0 then recordBytes.[0] else byte 0
        Result.Error
            { CommandId = commandId
              RecordIndex = recordIndex
              ByteOffset = 0
              Category = InvalidUtf8 (badByte, 0)
              SafeBytesHex = hexBytes recordBytes }
    | ex ->
        let badByte =
            if recordBytes.Length > 0 then recordBytes.[0] else byte 0
        Result.Error
            { CommandId = commandId
              RecordIndex = recordIndex
              ByteOffset = 0
              Category = InvalidUtf8 (badByte, 0)
              SafeBytesHex = hexBytes recordBytes }

let parse (commandId: string) (bytes: byte[]) : ParseResult =
    if bytes.Length = 0 then
        Ok []
    else
        let records = ResizeArray<byte[]>()
        let mutable start = 0
        let mutable i = 0
        let mutable sawNul = false
        while i < bytes.Length do
            if bytes.[i] = byte 0 then
                let len = i - start
                records.Add(Array.sub bytes start len)
                start <- i + 1
                sawNul <- true
                i <- i + 1
            else
                i <- i + 1

        let finalLen = bytes.Length - start
        if finalLen > 0 then
            Error
                { CommandId = commandId
                  RecordIndex = -1
                  ByteOffset = start
                  Category = UnterminatedFinalRecord finalLen
                  SafeBytesHex = "" }
        else if not sawNul then
            Error
                { CommandId = commandId
                  RecordIndex = -1
                  ByteOffset = 0
                  Category = UnterminatedFinalRecord bytes.Length
                  SafeBytesHex = "" }
        else
            let decoded = ResizeArray<string>()
            let mutable index = 0
            let mutable failure : DecodeDiagnostic option = None
            for r in records do
                if failure.IsSome then ()
                elif r.Length = 0 then
                    () // empty record from consecutive/trailing NULs; collapse
                else
                    match decodeStrict r index commandId with
                    | Result.Ok s -> decoded.Add s
                    | Result.Error d -> failure <- Some d
                index <- index + 1
            match failure with
            | Some d -> Error d
            | None ->
                Ok (List.ofSeq decoded)

let renderDiagnostic (d: DecodeDiagnostic) : string =
    let categoryText =
        match d.Category with
        | InvalidUtf8 (b, _) ->
            sprintf "invalid_utf8 byte=0x%02x" b
        | UnterminatedFinalRecord n ->
            sprintf "unterminated_final_record length=%d" n
        | EmptyInteriorRecord i ->
            sprintf "empty_interior_record index=%d" i
    let safeHex =
        if d.SafeBytesHex = "" then ""
        else sprintf " hex=%s" (sanitiseAscii d.SafeBytesHex)
    sprintf "command=%s record=%d offset=%d category=%s%s"
        d.CommandId d.RecordIndex d.ByteOffset categoryText safeHex