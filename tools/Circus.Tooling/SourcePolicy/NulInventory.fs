module Circus.Tooling.SourcePolicy.NulInventory

/// Pure NUL-delimited record parser.
///
/// ``git ls-files -z`` emits filenames as opaque, non-NUL byte
/// sequences terminated by NUL.  The parser is pure over bytes: it
/// never touches the file system, never spawns a process, and never
/// depends on character encoding until the final UTF-8 decode step.
///
/// The inventory contract is intentionally narrow: every record is
/// non-empty, NULs are record separators only, an unterminated final
/// nonempty record is a hard error (because ``git ls-files -z``
/// always terminates its last record with NUL), and the encoding is
/// UTF-8 with **no** silent replacement of invalid byte sequences.

open System
open System.Text

// -----------------------------------------------------------------------------
// Diagnostic types
// -----------------------------------------------------------------------------

/// Failure category for invalid path decoding.  The verifier MUST
/// fail closed when any of these appear.
type DecodeFailure =
    | InvalidUtf8 of badByte: byte * byteOffset: int
    | UnterminatedFinalRecord of expected: int
    | EmptyInteriorRecord of recordIndex: int

/// Diagnostic returned for a single invalid record.
type DecodeDiagnostic = {
    CommandId: string
    RecordIndex: int
    ByteOffset: int
    Category: DecodeFailure
    /// Safe string representation of the byte sequence (lossy hex).
    SafeBytesHex: string
}

/// Outcome of parsing the byte stream.  ``Ok`` carries the decoded
/// paths in record order.  ``Error`` carries the first diagnostic;
/// the parser stops at the first failure so the failure shape is
/// deterministic.
type ParseResult =
    | Ok of paths: string list
    | Error of diagnostic: DecodeDiagnostic

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

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

/// Walk the byte buffer to find a UTF-8 boundary error.  Returns the
/// byte offset of the first invalid byte, or ``None`` when the entire
/// buffer decodes cleanly.  The check is strict: invalid sequences
/// are reported rather than replaced.
let private findInvalidUtf8 (bytes: byte[]) : (byte * int) option =
    let mutable decoder = Encoding.UTF8.GetDecoder()
    let mutable i = 0
    let chunk : byte[] = Array.zeroCreate 1
    let chars : char[] = Array.zeroCreate 4
    let mutable failed : (byte * int) option = None
    while i < bytes.Length && failed.IsNone do
        chunk.[0] <- bytes.[i]
        let mutable bytesUsed = 0
        let mutable charsUsed = 0
        let mutable completed = false
        let ok =
            try
                let _ =
                    decoder.Convert(chunk, 0, 1, chars, 0, chars.Length, false,
                                    &bytesUsed, &charsUsed, &completed)
                true
            with _ ->
                false
        if not ok then
            failed <- Some (bytes.[i], i)
        i <- i + 1
    failed

// -----------------------------------------------------------------------------
// Pure parser
// -----------------------------------------------------------------------------

/// Split a NUL-delimited byte stream into non-empty UTF-8 records.
///
/// Strictly fails closed when:
///   * a record contains invalid UTF-8 byte sequences;
///   * the final record is nonempty and unterminated by a NUL;
///   * an empty interior record is observed.
///
/// The trailing NUL is consumed silently; the parser does not return
/// a phantom empty path for it.  Records appear in source order.
let parse (commandId: string) (bytes: byte[]) : ParseResult =
    if bytes.Length = 0 then
        Ok []
    else
        // Frame on NUL (byte 0x00).
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
            // Final nonempty record must be terminated by NUL;
            // ``git ls-files -z`` always terminates its last record.
            Error
                { CommandId = commandId
                  RecordIndex = -1
                  ByteOffset = start
                  Category = UnterminatedFinalRecord finalLen
                  SafeBytesHex = "" }
        else if not sawNul then
            // No NUL framing at all — also treated as unterminated.
            Error
                { CommandId = commandId
                  RecordIndex = -1
                  ByteOffset = 0
                  Category = UnterminatedFinalRecord bytes.Length
                  SafeBytesHex = "" }
        else
            // Decode each non-empty record strictly.
            let decoded = ResizeArray<string>()
            let mutable index = 0
            let mutable failure : DecodeDiagnostic option = None
            for r in records do
                if failure.IsSome then ()
                elif r.Length = 0 then
                    failure <- Some
                        { CommandId = commandId
                          RecordIndex = index
                          ByteOffset = 0
                          Category = EmptyInteriorRecord index
                          SafeBytesHex = "" }
                else
                    match findInvalidUtf8 r with
                    | Some (b, off) ->
                        failure <- Some
                            { CommandId = commandId
                              RecordIndex = index
                              ByteOffset = off
                              Category = InvalidUtf8 (b, off)
                              SafeBytesHex = hexBytes r }
                    | None ->
                        let s = (UTF8Encoding(false, true)).GetString(r)
                        if s = "" then
                            failure <- Some
                                { CommandId = commandId
                                  RecordIndex = index
                                  ByteOffset = 0
                                  Category = EmptyInteriorRecord index
                                  SafeBytesHex = "" }
                        else
                            decoded.Add s
                index <- index + 1
            match failure with
            | Some d -> Error d
            | None ->
                Ok (List.ofSeq decoded)

/// Render a deterministic diagnostic line for a ``DecodeDiagnostic``.
/// The line MUST NOT contain unsafe terminal-control bytes — the
/// output is sanitised to printable ASCII plus space, dash, comma,
/// and colon.
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