module Circus.Tooling.SourcePolicy.NulInventory

/// Pure NUL-delimited record parser.

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

let private decodeStrict (recordBytes: byte[]) (recordIndex: int) (commandId: string) (recordStartOffset: int) : Result<string, DecodeDiagnostic> =
    let strict = UTF8Encoding(false, true)
    if recordBytes.Length = 0 then
        Result.Error
            { CommandId = commandId
              RecordIndex = recordIndex
              ByteOffset = recordStartOffset
              Category = EmptyInteriorRecord recordIndex
              SafeBytesHex = "" }
    else
        try
            let s = strict.GetString(recordBytes)
            Result.Ok s
        with
        | :? DecoderFallbackException as ex ->
            let localOffset =
                if ex.Index >= 0 && ex.Index < recordBytes.Length then ex.Index
                else 0
            let globalOffset = recordStartOffset + localOffset
            let badByte =
                if not (isNull ex.BytesUnknown) && ex.BytesUnknown.Length > 0 then
                    ex.BytesUnknown.[0]
                elif localOffset < recordBytes.Length then
                    recordBytes.[localOffset]
                else
                    recordBytes.[0]
            Result.Error
                { CommandId = commandId
                  RecordIndex = recordIndex
                  ByteOffset = globalOffset
                  Category = InvalidUtf8 (badByte, globalOffset)
                  SafeBytesHex = hexBytes recordBytes }
        | ex ->
            let badByte = if recordBytes.Length > 0 then recordBytes.[0] else byte 0
            Result.Error
                { CommandId = commandId
                  RecordIndex = recordIndex
                  ByteOffset = recordStartOffset
                  Category = InvalidUtf8 (badByte, recordStartOffset)
                  SafeBytesHex = hexBytes recordBytes }

let parse (commandId: string) (bytes: byte[]) : ParseResult =
    if bytes.Length = 0 then
        Ok []
    else
        let records = ResizeArray<byte[] * int>()
        let mutable start = 0
        let mutable i = 0
        let mutable sawNul = false
        while i < bytes.Length do
            if bytes.[i] = byte 0 then
                let len = i - start
                records.Add (Array.sub bytes start len, start)
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
        elif not sawNul then
            Error
                { CommandId = commandId
                  RecordIndex = -1
                  ByteOffset = 0
                  Category = UnterminatedFinalRecord bytes.Length
                  SafeBytesHex = "" }
        else
            // A single NUL terminator with no payload is the empty
            // inventory, not an empty interior record.  The empty
            // record check is only meaningful when at least one
            // non-empty record is also present.
            let hasRealRecord =
                records
                |> Seq.exists (fun (rb, _) -> rb.Length > 0)
            let decoded = ResizeArray<string>()
            let mutable index = 0
            let mutable failure : DecodeDiagnostic option = None
            for (rb, offset) in records do
                if failure.IsSome then ()
                elif rb.Length = 0 && hasRealRecord then
                    failure <- Some
                        { CommandId = commandId
                          RecordIndex = index
                          ByteOffset = offset
                          Category = EmptyInteriorRecord index
                          SafeBytesHex = "" }
                elif rb.Length = 0 then
                    () // empty inventory case
                else
                    match decodeStrict rb index commandId offset with
                    | Result.Ok s -> decoded.Add s
                    | Result.Error d -> failure <- Some d
                index <- index + 1
            match failure with
            | Some d -> Error d
            | None -> Ok (List.ofSeq decoded)

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
