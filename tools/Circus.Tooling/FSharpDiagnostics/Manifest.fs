module Circus.Tooling.FSharpDiagnostics.Manifest

open System.IO
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Inventory
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.Serialization

// =============================================================================
// Minimal JSON parser (just enough for the well-formed manifest files this
// ACT writes itself; we deliberately avoid pulling in a JSON library to keep
// the foundation dependency footprint minimal).
// =============================================================================

type JsonValue =
    | JsonNull
    | JsonBool of bool
    | JsonNumber of string
    | JsonString of string
    | JsonArray of JsonValue list
    | JsonObject of (string * JsonValue) list

exception JsonParseException of int * string

let private skipWs (s: string) (i: int) : int =
    let mutable j = i
    while j < s.Length && System.Char.IsWhiteSpace s.[j] do
        j <- j + 1
    j

let rec private parseValue (s: string) (i: int) : JsonValue * int =
    let i = skipWs s i
    if i >= s.Length then raise (JsonParseException(i, "unexpected EOF"))
    match s.[i] with
    | 'n' ->
        if i + 4 <= s.Length && s.Substring(i, 4) = "null" then
            JsonNull, i + 4
        else raise (JsonParseException(i, "expected null"))
    | 't' ->
        if i + 4 <= s.Length && s.Substring(i, 4) = "true" then
            JsonBool true, i + 4
        else raise (JsonParseException(i, "expected true"))
    | 'f' ->
        if i + 5 <= s.Length && s.Substring(i, 5) = "false" then
            JsonBool false, i + 5
        else raise (JsonParseException(i, "expected false"))
    | '"' -> parseString s i
    | '[' -> parseArray s i
    | '{' -> parseObject s i
    | c when c = '-' || (c >= '0' && c <= '9') -> parseNumber s i
    | c -> raise (JsonParseException(i, sprintf "unexpected character '%c'" c))

and private parseString (s: string) (i: int) : JsonValue * int =
    if s.[i] <> '"' then raise (JsonParseException(i, "expected '\"'"))
    let sb = System.Text.StringBuilder()
    let mutable j = i + 1
    let mutable finished = false
    while not finished && j < s.Length do
        match s.[j] with
        | '"' -> finished <- true
        | '\\' ->
            if j + 1 >= s.Length then raise (JsonParseException(j, "truncated escape"))
            let c = s.[j + 1]
            match c with
            | '"' -> sb.Append '"' |> ignore
            | '\\' -> sb.Append '\\' |> ignore
            | '/' -> sb.Append '/' |> ignore
            | 'n' -> sb.Append '\n' |> ignore
            | 't' -> sb.Append '\t' |> ignore
            | 'r' -> sb.Append '\r' |> ignore
            | 'b' -> sb.Append '\b' |> ignore
            | 'f' -> sb.Append '\x0c' |> ignore
            | 'u' ->
                if j + 6 > s.Length then
                    raise (JsonParseException(j, "truncated unicode escape"))
                let hex = s.Substring(j + 2, 4)
                let code = System.Convert.ToInt32(hex, 16)
                sb.Append(System.Char.ConvertFromUtf32(code)) |> ignore
                j <- j + 4
            | _ -> raise (JsonParseException(j, sprintf "invalid escape \\%c" c))
            j <- j + 2
        | c -> sb.Append c |> ignore; j <- j + 1
    if not finished then raise (JsonParseException(j, "unterminated string"))
    JsonString(sb.ToString()), j + 1

and private parseNumber (s: string) (i: int) : JsonValue * int =
    let mutable j = i
    if s.[j] = '-' then j <- j + 1
    while j < s.Length
          && (s.[j] >= '0' && s.[j] <= '9'
              || s.[j] = '.'
              || s.[j] = 'e'
              || s.[j] = 'E'
              || s.[j] = '+'
              || s.[j] = '-') do
        j <- j + 1
    JsonNumber(s.Substring(i, j - i)), j

and private parseArray (s: string) (i: int) : JsonValue * int =
    if s.[i] <> '[' then raise (JsonParseException(i, "expected '['"))
    let mutable j = i + 1
    let items = System.Collections.Generic.List<JsonValue>()
    j <- skipWs s j
    if j < s.Length && s.[j] = ']' then
        JsonArray(items |> Seq.toList), j + 1
    else
        let mutable doneParsing = false
        while not doneParsing do
            let v, afterV = parseValue s j
            items.Add v
            j <- skipWs s afterV
            if j < s.Length && s.[j] = ',' then
                j <- j + 1
            elif j < s.Length && s.[j] = ']' then
                doneParsing <- true
                j <- j + 1
            else
                raise (JsonParseException(j, "expected ',' or ']'"))
        JsonArray(items |> Seq.toList), j

and private parseObject (s: string) (i: int) : JsonValue * int =
    if s.[i] <> '{' then raise (JsonParseException(i, "expected '{'"))
    let mutable j = i + 1
    let fields = System.Collections.Generic.List<string * JsonValue>()
    j <- skipWs s j
    if j < s.Length && s.[j] = '}' then
        JsonObject(fields |> Seq.toList), j + 1
    else
        let mutable doneParsing = false
        while not doneParsing do
            let key, afterKey = parseString s j
            let keyText =
                match key with
                | JsonString t -> t
                | _ -> raise (JsonParseException(j, "expected string key"))
            j <- skipWs s afterKey
            if j >= s.Length || s.[j] <> ':' then
                raise (JsonParseException(j, "expected ':'"))
            j <- j + 1
            let value, afterValue = parseValue s j
            fields.Add (keyText, value)
            j <- skipWs s afterValue
            if j < s.Length && s.[j] = ',' then
                j <- j + 1
            elif j < s.Length && s.[j] = '}' then
                doneParsing <- true
                j <- j + 1
            else
                raise (JsonParseException(j, "expected ',' or '}'"))
        JsonObject(fields |> Seq.toList), j

let parseJson (s: string) : JsonValue =
    let v, after = parseValue s 0
    let afterWs = skipWs s after
    if afterWs <> s.Length then
        raise (JsonParseException(afterWs, "trailing content after JSON value"))
    v

// =============================================================================
// Field access helpers
// =============================================================================

let private asObject (v: JsonValue) : (string * JsonValue) list =
    match v with
    | JsonObject fields -> fields
    | _ -> raise (JsonParseException(0, "expected JSON object"))

let private field (o: (string * JsonValue) list) (name: string) : JsonValue option =
    o |> List.tryPick (fun (k, v) -> if k = name then Some v else None)

let private asString (v: JsonValue) : string =
    match v with
    | JsonString s -> s
    | _ -> raise (JsonParseException(0, "expected string"))

let private asOptString (v: JsonValue option) : string option =
    match v with
    | Some (JsonString s) -> Some s
    | Some JsonNull -> None
    | Some _ -> raise (JsonParseException(0, "expected string or null"))
    | None -> None

let private asInt (v: JsonValue) : int =
    match v with
    | JsonNumber n -> System.Convert.ToInt32(n, System.Globalization.CultureInfo.InvariantCulture)
    | _ -> raise (JsonParseException(0, "expected integer"))

let private asOptInt (v: JsonValue option) : int option =
    match v with
    | Some (JsonNumber n) ->
        Some(System.Convert.ToInt32(n, System.Globalization.CultureInfo.InvariantCulture))
    | Some JsonNull -> None
    | Some _ -> raise (JsonParseException(0, "expected integer or null"))
    | None -> None

let private asLong (v: JsonValue) : int64 =
    match v with
    | JsonNumber n -> System.Convert.ToInt64(n, System.Globalization.CultureInfo.InvariantCulture)
    | _ -> raise (JsonParseException(0, "expected integer"))

let private asStringList (v: JsonValue) : string list =
    match v with
    | JsonArray items ->
        items |> List.map (fun it ->
            match it with
            | JsonString s -> s
            | _ -> raise (JsonParseException(0, "expected string in array")))
    | _ -> raise (JsonParseException(0, "expected array"))

let private asAliases (v: JsonValue option) : SourceRootAlias list =
    match v with
    | None
    | Some JsonNull -> []
    | Some (JsonArray items) ->
        items |> List.map (fun it ->
            let fields = asObject it
            { AbsoluteRoot = asString (field fields "absolute_root" |> Option.defaultValue JsonNull)
              CanonicalRoot = asString (field fields "canonical_root" |> Option.defaultValue JsonNull) })
    | _ -> raise (JsonParseException(0, "expected alias array"))

// =============================================================================
// Capture manifest IO
// =============================================================================

let readCaptureManifest (path: string) : CaptureManifest =
    let text = File.ReadAllText path
    let json = parseJson text
    let o = asObject json
    { SchemaVersion = asString (field o "schema_version" |> Option.defaultValue JsonNull)
      CaptureId = asString (field o "capture_id" |> Option.defaultValue JsonNull)
      CaptureKind = asString (field o "capture_kind" |> Option.defaultValue JsonNull)
      RawArtifacts =
        match field o "raw_artifacts" with
        | Some v -> asStringList v
        | None -> []
      Command = asOptString (field o "command")
      WorkingDirectory = asOptString (field o "working_directory")
      RepositoryCommitOid = asOptString (field o "repository_commit_oid")
      RepositoryTreeOid = asOptString (field o "repository_tree_oid")
      WorkingTreeState = asOptString (field o "working_tree_state")
      SourceRootAliases = asAliases (field o "source_root_aliases")
      DotnetSdkVersion = asOptString (field o "dotnet_sdk_version")
      MsbuildVersion = asOptString (field o "msbuild_version")
      FsharpCompilerVersion = asOptString (field o "fsharp_compiler_version")
      OperatingSystem = asOptString (field o "operating_system")
      Architecture = asOptString (field o "architecture")
      Culture = asOptString (field o "culture")
      StartedAt = asOptString (field o "started_at")
      CompletedAt = asOptString (field o "completed_at")
      ExitCode = asOptInt (field o "exit_code")
      MetadataGaps =
        match field o "metadata_gaps" with
        | Some v -> asStringList v
        | None -> [] }

let writeCaptureManifest (path: string) (m: CaptureManifest) : unit =
    let text = renderCaptureManifest m
    writeLineOriented path text

// =============================================================================
// Artifact manifest IO (jsonl)
// =============================================================================

let readArtifactManifestEntries (path: string) : ArtifactManifestEntry list =
    if not (File.Exists path) then []
    else
        let lines =
            File.ReadAllLines path
            |> Array.filter (fun l -> not (System.String.IsNullOrWhiteSpace l))
        lines |> Array.toList |> List.map (fun line ->
            let json = parseJson line
            let o = asObject json
            { SchemaVersion = asString (field o "schema_version" |> Option.defaultValue JsonNull)
              CanonicalPath = asString (field o "canonical_path" |> Option.defaultValue JsonNull)
              OriginalPath = asString (field o "original_path" |> Option.defaultValue JsonNull)
              ArtifactClass = asString (field o "artifact_class" |> Option.defaultValue JsonNull)
              Authority = asString (field o "authority" |> Option.defaultValue JsonNull)
              Status = asString (field o "status" |> Option.defaultValue JsonNull)
              MediaType = asString (field o "media_type" |> Option.defaultValue JsonNull)
              ByteLength =
                asLong (field o "byte_length" |> Option.defaultValue (JsonNumber "0"))
              Sha256 = asString (field o "sha256" |> Option.defaultValue JsonNull)
              CaptureId = asOptString (field o "capture_id")
              Supersedes = asOptString (field o "supersedes")
              SupersededBy = asOptString (field o "superseded_by")
              MetadataGaps =
                match field o "metadata_gaps" with
                | Some v -> asStringList v
                | None -> [] })

let writeArtifactManifest (path: string) (entries: ArtifactManifestEntry list) : unit =
    let sorted =
        entries
        |> List.sortBy (fun e -> e.CanonicalPath)
    let lines =
        sorted
        |> List.map renderArtifactManifestEntry
        |> String.concat "\n"
    writeLineOriented path lines