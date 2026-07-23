module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.CaptureBinding

// =============================================================================
// Strict capture binding
// =============================================================================
//
// This module binds one declared capture to the canonical evidence the
// repair-episode foundation consumes:
//
//   1. its canonical capture manifest (`capture.json`);
//   2. an externally resolved Git commit OID;
//   3. an externally resolved Git tree OID;
//   4. its declared raw artifact bytes;
//   5. the foundation artifact manifest entries for those bytes;
//   6. its strictly parsed diagnostic occurrences.
//
// The slice is read-only and side-effect-free.  It deliberately does not run
// Git.  The caller (a later engine slice) supplies the already resolved
// commit and tree identities obtained through Git's ``^{commit}`` and
// ``^{tree}`` resolution forms.
//
// The public boundary is ``Result<BoundCapture, CaptureBindingFailure>``.
// The failure union is closed: every rejection mode has a dedicated case so
// downstream callers can branch on the exact cause without parsing strings.
//
// Canonical paths emitted in the result and in every identity-bearing failure
// are repository-relative (forward slashes, no leading slash).  Two
// ``BoundCapture`` records produced against equivalent capture directories
// that lie under different repository roots are equal.

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.OccurrenceReader
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths

// =============================================================================
// Public model
// =============================================================================

type CaptureBindingRequest = {
    CaptureId: string
    ResolvedCommitOid: string
    ResolvedTreeOid: string
    ExpectedTreeOid: string option
}

type BoundRawArtifact = {
    CanonicalPath: string
    ByteLength: int64
    Sha256: string
}

type BoundCapture = {
    Manifest: CaptureManifest
    Occurrences: DiagnosticOccurrence list
    RawArtifacts: BoundRawArtifact list
}

// =============================================================================
// Failure model
// =============================================================================

type CaptureBindingFailure =
    | InvalidCaptureId of captureId: string
    | CaptureManifestMissing of canonicalPath: string
    | CaptureManifestReadFailed of canonicalPath: string * detail: string
    | CaptureIdMismatch of requested: string * manifest: string

    | RepositoryCommitOidMissing of captureId: string
    | RepositoryCommitOidMismatch of expected: string * actual: string
    | RepositoryTreeOidMissing of captureId: string
    | RepositoryTreeOidMismatch of expected: string * actual: string
    | ExpectedTreeOidMismatch of expected: string * resolved: string

    | RawArtifactListEmpty of captureId: string
    | RawArtifactPathInvalid of path: string
    | DuplicateRawArtifactPath of path: string
    | RawArtifactMissing of canonicalPath: string
    | RawArtifactReadFailed of canonicalPath: string * detail: string

    | ArtifactManifestMissing of canonicalPath: string
    | ArtifactManifestReadFailed of canonicalPath: string * lineNumber: int * detail: string
    | ArtifactManifestEntryMissing of canonicalPath: string
    | ArtifactManifestEntryDuplicate of canonicalPath: string
    | ArtifactManifestCaptureMismatch of canonicalPath: string
    | ArtifactManifestStatusMismatch of canonicalPath: string
    | ArtifactManifestClassMismatch of canonicalPath: string
    | RawArtifactLengthMismatch of canonicalPath: string * expected: int64 * actual: int64
    | RawArtifactHashMismatch of canonicalPath: string * expected: string * actual: string

    | OccurrenceStreamFailure of OccurrenceReadFailure
    | DuplicateOccurrenceOrdinal of captureId: string * ordinal: int64

// =============================================================================
// Constants
// =============================================================================

let private rawArtifactCanonicalClass = "raw"
let private presentCanonicalStatus = "present"

// =============================================================================
// Capture ID safety
// =============================================================================

let private isValidCaptureId (captureId: string) : bool =
    if String.IsNullOrWhiteSpace captureId then false
    elif isAbsolute captureId then false
    elif captureId.Contains "/" || captureId.Contains "\\" then false
    elif captureId = "." || captureId = ".." then false
    elif captureId.IndexOf '\u0000' >= 0 then false
    else true

// =============================================================================
// Raw artifact path safety
// =============================================================================

let private isValidRawArtifactPath (rawPath: string) : bool =
    if String.IsNullOrEmpty rawPath then false
    elif rawPath.Contains "\\" then false
    elif isAbsolute rawPath then false
    else
        let segments = rawPath.Split([| '/' |])
        let mutable ok = true
        let mutable i = 0
        while ok && i < segments.Length do
            let seg = segments.[i]
            if seg = "." || seg = ".." then ok <- false
            i <- i + 1
        ok

let private captureCanonicalDir (captureId: string) : string =
    canonicalise (rawSubdir + "/" + captureId)

// =============================================================================
// Symlink / junction rejection
// =============================================================================
//
// ``FileAttributes.ReparsePoint`` is set on both symbolic links and NTFS
// junctions.  The capture-binding slice refuses to read a raw file when the
// final file or any intermediate directory between the capture directory and
// the file resolves through a reparse point.  This protects the binding
// from being tricked into reading attacker-controlled content that lives
// outside the declared capture.

// ``isReparsePoint`` is fail-closed: an inability to inspect the file
// attributes is propagated as ``PathInspectionFailed`` so the binding
// refuses to read a file whose reparse-point status is unknown.  Treating
// inspection failure as "definitely not a reparse point" would let a
// filesystem-level error silently pass through as a normal file.
exception private PathInspectionFailed of string

let private isReparsePoint (path: string) : bool =
    try
        let attrs = File.GetAttributes(path)
        (attrs &&& FileAttributes.ReparsePoint) <> FileAttributes.None
    with
    | :? IOException as ex -> raise (PathInspectionFailed ex.Message)
    | :? UnauthorizedAccessException as ex -> raise (PathInspectionFailed ex.Message)

/// Walk from the file's parent directory up to and including the capture
/// directory, returning ``Some`` with the first directory that is a
/// reparse point, or ``None`` if every directory is a normal directory.
/// Inspection failures bubble up as ``PathInspectionFailed``.
let private findReparsePointBetween
    (fileParent: string)
    (captureAbsDir: string)
    : string option =
    let stop = Path.GetFullPath captureAbsDir
    let mutable current = fileParent
    let mutable found : string option = None
    let mutable finished = false
    while not finished && found.IsNone do
        if isReparsePoint current then
            found <- Some current
        else
            let normalized = Path.GetFullPath current
            if String.Equals(normalized, stop, StringComparison.OrdinalIgnoreCase) then
                finished <- true
            else
                let parent = Path.GetDirectoryName current
                if parent = current then
                    finished <- true  // walked all the way to filesystem root
                else
                    current <- parent
    found

// =============================================================================
// Strict artifact manifest reader
// =============================================================================
//
// The foundation ``Manifest.readArtifactManifestEntries`` reader is not
// strict enough:
//
//   * it does not reject unknown fields;
//   * it does not detect duplicate property names;
//   * it does not preserve one-based line numbers in errors;
//   * it silently skips records when ``field`` returns ``None``;
//   * it does not distinguish absent and null for required fields;
//   * it does not validate ``schema_version``;
//   * it does not reject BOM or NUL bytes.
//
// This private reader implements the strict contract required by the
// binding: malformed nonblank records abort the entire file, the one-based
// line number is preserved, duplicate and unknown properties are rejected,
// absent and null are distinguished for every required field, BOM and NUL
// bytes are rejected, and the schema version is validated.
//
// The decoder is ``UTF8Encoding(false, true)``: no BOM emission and throw
// on invalid byte sequences.  Combined with the explicit BOM rejection via
// the leading-three-byte check, this means any non-canonical byte stream
// is rejected.

// ``ArtifactManifestReadFailure`` is internal so the private exception that
// carries it does not violate F# accessibility rules.  The public result
// surface is ``Result<ArtifactManifestEntry list, ArtifactManifestReadFailure>``,
// which is itself private; callers that bridge the binding's failure
// surface convert these cases into public ``CaptureBindingFailure`` cases.
type internal ArtifactManifestReadFailure =
    | FileMissing of canonicalPath: string
    | FileReadFailed of canonicalPath: string * detail: string
    | InvalidByteSequence of canonicalPath: string * detail: string
    | BomPresent of canonicalPath: string
    | NulBytePresent of canonicalPath: string
    | InvalidJson of canonicalPath: string * lineNumber: int * detail: string
    | RootNotObject of canonicalPath: string * lineNumber: int
    | MissingField of canonicalPath: string * lineNumber: int * field: string
    | DuplicateField of canonicalPath: string * lineNumber: int * field: string
    | UnknownField of canonicalPath: string * lineNumber: int * field: string
    | WrongJsonKind of canonicalPath: string * lineNumber: int * field: string
    | SchemaVersionMismatch of canonicalPath: string * lineNumber: int * actual: string

exception private ArtifactManifestParseAbort of ArtifactManifestReadFailure

let private artifactManifestFail (failure: ArtifactManifestReadFailure) : 'a =
    raise (ArtifactManifestParseAbort failure)

let private artifactManifestKnownFields : Set<string> =
    set [
        "schema_version"
        "canonical_path"
        "original_path"
        "artifact_class"
        "authority"
        "status"
        "media_type"
        "byte_length"
        "sha256"
        "capture_id"
        "supersedes"
        "superseded_by"
        "metadata_gaps"
    ]

let private artifactManifestDecoder : UTF8Encoding =
    UTF8Encoding(false, true)

let private artifactManifestStartsWithBom (bytes: byte[]) : bool =
    bytes.Length >= 3
    && bytes.[0] = 0xEFuy
    && bytes.[1] = 0xBBuy
    && bytes.[2] = 0xBFuy

let private artifactManifestContainsNul (bytes: byte[]) : bool =
    Array.exists (fun b -> b = 0uy) bytes

let private tryGetProperty (root: JsonElement) (name: string) : JsonElement option =
    let mutable result = Unchecked.defaultof<JsonElement>
    if root.TryGetProperty(name, &result) then
        Some result
    else
        None

let private checkArtifactManifestDuplicates
    (path: string)
    (lineNumber: int)
    (root: JsonElement)
    : unit =
    let seen = HashSet<string>(StringComparer.Ordinal)
    let mutable dup : string option = None
    for prop in root.EnumerateObject() do
        if dup.IsNone && not (seen.Add prop.Name) then
            dup <- Some prop.Name
    match dup with
    | Some n -> artifactManifestFail (DuplicateField (path, lineNumber, n))
    | None -> ()

let private checkArtifactManifestUnknown
    (path: string)
    (lineNumber: int)
    (root: JsonElement)
    : unit =
    let firstUnknown =
        root.EnumerateObject()
        |> Seq.tryPick (fun p ->
            if Set.contains p.Name artifactManifestKnownFields then None
            else Some p.Name)
    match firstUnknown with
    | Some n -> artifactManifestFail (UnknownField (path, lineNumber, n))
    | None -> ()

let private artifactManifestRequiredString
    (path: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : string =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> artifactManifestFail (MissingField (path, lineNumber, field))
    if el.ValueKind = JsonValueKind.Null then
        artifactManifestFail (WrongJsonKind (path, lineNumber, field))
    elif el.ValueKind <> JsonValueKind.String then
        artifactManifestFail (WrongJsonKind (path, lineNumber, field))
    else
        el.GetString()

let private artifactManifestRequiredNullableString
    (path: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : string option =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> artifactManifestFail (MissingField (path, lineNumber, field))
    if el.ValueKind = JsonValueKind.Null then
        None
    elif el.ValueKind <> JsonValueKind.String then
        artifactManifestFail (WrongJsonKind (path, lineNumber, field))
    else
        Some (el.GetString())

let private artifactManifestRequiredInt64
    (path: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : int64 =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> artifactManifestFail (MissingField (path, lineNumber, field))
    if el.ValueKind <> JsonValueKind.Number then
        artifactManifestFail (WrongJsonKind (path, lineNumber, field))
    let raw = el.GetRawText()
    if raw.Contains "." || raw.Contains "e" || raw.Contains "E" then
        artifactManifestFail (WrongJsonKind (path, lineNumber, field))
    try
        Int64.Parse(raw, CultureInfo.InvariantCulture)
    with _ ->
        artifactManifestFail (WrongJsonKind (path, lineNumber, field))

let private artifactManifestRequiredStringList
    (path: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : string list =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> artifactManifestFail (MissingField (path, lineNumber, field))
    if el.ValueKind = JsonValueKind.Null then
        artifactManifestFail (WrongJsonKind (path, lineNumber, field))
    elif el.ValueKind <> JsonValueKind.Array then
        artifactManifestFail (WrongJsonKind (path, lineNumber, field))
    else
        let mutable acc : string list = []
        for item in el.EnumerateArray() do
            if item.ValueKind <> JsonValueKind.String then
                artifactManifestFail (WrongJsonKind (path, lineNumber, field))
            else
                acc <- (item.GetString()) :: acc
        List.rev acc

let private parseArtifactManifestLine
    (path: string)
    (lineNumber: int)
    (line: string)
    : ArtifactManifestEntry =
    let doc =
        try
            JsonDocument.Parse(line)
        with
        | :? JsonException as ex ->
            artifactManifestFail (InvalidJson (path, lineNumber, ex.Message))

    use doc = doc
    let root = doc.RootElement
    if root.ValueKind <> JsonValueKind.Object then
        artifactManifestFail (RootNotObject (path, lineNumber))
    checkArtifactManifestDuplicates path lineNumber root
    checkArtifactManifestUnknown path lineNumber root

    let schemaVersion = artifactManifestRequiredString path lineNumber "schema_version" root
    if schemaVersion <> ArtifactManifestSchemaVersion then
        artifactManifestFail (SchemaVersionMismatch (path, lineNumber, schemaVersion))
    let canonicalPath = artifactManifestRequiredString path lineNumber "canonical_path" root
    let originalPath = artifactManifestRequiredString path lineNumber "original_path" root
    let artifactClass = artifactManifestRequiredString path lineNumber "artifact_class" root
    let authority = artifactManifestRequiredString path lineNumber "authority" root
    let status = artifactManifestRequiredString path lineNumber "status" root
    let mediaType = artifactManifestRequiredString path lineNumber "media_type" root
    let byteLength = artifactManifestRequiredInt64 path lineNumber "byte_length" root
    let sha256 = artifactManifestRequiredString path lineNumber "sha256" root
    let captureId = artifactManifestRequiredNullableString path lineNumber "capture_id" root
    let supersedes = artifactManifestRequiredNullableString path lineNumber "supersedes" root
    let supersededBy = artifactManifestRequiredNullableString path lineNumber "superseded_by" root
    let metadataGaps = artifactManifestRequiredStringList path lineNumber "metadata_gaps" root

    { SchemaVersion = schemaVersion
      CanonicalPath = canonicalPath
      OriginalPath = originalPath
      ArtifactClass = artifactClass
      Authority = authority
      Status = status
      MediaType = mediaType
      ByteLength = byteLength
      Sha256 = sha256
      CaptureId = captureId
      Supersedes = supersedes
      SupersededBy = supersededBy
      MetadataGaps = metadataGaps }

let private readArtifactManifestStrict
    (path: string)
    : Result<ArtifactManifestEntry list, ArtifactManifestReadFailure> =
    if not (File.Exists path) then
        Result.Error (FileMissing path)
    else
        let bytesResult =
            try
                Result.Ok (File.ReadAllBytes path)
            with
            | :? IOException as ex -> Result.Error (FileReadFailed (path, ex.Message))
            | :? UnauthorizedAccessException as ex -> Result.Error (FileReadFailed (path, ex.Message))
        match bytesResult with
        | Result.Error e -> Result.Error e
        | Result.Ok bytes ->
            if artifactManifestStartsWithBom bytes then
                Result.Error (BomPresent path)
            elif artifactManifestContainsNul bytes then
                Result.Error (NulBytePresent path)
            else
                let text =
                    try
                        Result.Ok (artifactManifestDecoder.GetString bytes)
                    with
                    | :? DecoderFallbackException as ex ->
                        Result.Error (InvalidByteSequence (path, ex.Message))
                match text with
                | Result.Error e -> Result.Error e
                | Result.Ok text ->
                    let normalised = StringBuilder(text.Length)
                    let mutable i = 0
                    while i < text.Length do
                        let c = text.[i]
                        if c = '\r' then
                            normalised.Append '\n' |> ignore
                            if i + 1 < text.Length && text.[i + 1] = '\n' then
                                i <- i + 1
                        else
                            normalised.Append c |> ignore
                        i <- i + 1
                    let lines = normalised.ToString().Split([| '\n' |], StringSplitOptions.None)
                    let mutable acc : ArtifactManifestEntry list = []
                    let mutable lineNumber = 0
                    let mutable failure : ArtifactManifestReadFailure = FileMissing path
                    let mutable aborted = false
                    for line in lines do
                        lineNumber <- lineNumber + 1
                        if not aborted then
                            if not (String.IsNullOrWhiteSpace line) then
                                try
                                    let entry = parseArtifactManifestLine path lineNumber line
                                    acc <- entry :: acc
                                with
                                | ArtifactManifestParseAbort f ->
                                    aborted <- true
                                    failure <- f
                    if aborted then
                        Result.Error failure
                    else
                        Result.Ok (acc |> List.rev)

// =============================================================================
// Strict capture manifest reader
// =============================================================================
//
// The foundation ``Manifest.readCaptureManifest`` reader is not strict:
// it does not reject unknown fields, does not detect duplicate property
// names, does not preserve one-based line numbers, silently substitutes
// empty strings for missing fields, does not validate ``schema_version``,
// and does not reject BOM or NUL bytes.  This private strict reader
// implements the contract required by the binding.
//
// ``readCaptureManifest`` is the strict entry point used by every code
// path inside this module.  Tests in ``CaptureBindingTests`` exercise both
// the reader directly and through ``bindCapture`` to prove strictness.

type internal CaptureManifestReadFailure =
    | FileMissing of canonicalPath: string
    | FileReadFailed of canonicalPath: string * detail: string
    | InvalidByteSequence of canonicalPath: string * detail: string
    | BomPresent of canonicalPath: string
    | NulBytePresent of canonicalPath: string
    | InvalidJson of canonicalPath: string * lineNumber: int * detail: string
    | RootNotObject of canonicalPath: string * lineNumber: int
    | MissingField of canonicalPath: string * lineNumber: int * field: string
    | DuplicateField of canonicalPath: string * lineNumber: int * field: string
    | UnknownField of canonicalPath: string * lineNumber: int * field: string
    | WrongJsonKind of canonicalPath: string * lineNumber: int * field: string
    | SchemaVersionMismatch of canonicalPath: string * lineNumber: int * actual: string

exception private CaptureManifestParseAbort of CaptureManifestReadFailure

let private captureManifestFail (failure: CaptureManifestReadFailure) : 'a =
    raise (CaptureManifestParseAbort failure)

let private captureManifestKnownFields : Set<string> =
    set [
        "schema_version"
        "capture_id"
        "capture_kind"
        "raw_artifacts"
        "command"
        "working_directory"
        "repository_commit_oid"
        "repository_tree_oid"
        "working_tree_state"
        "source_root_aliases"
        "dotnet_sdk_version"
        "msbuild_version"
        "fsharp_compiler_version"
        "operating_system"
        "architecture"
        "culture"
        "started_at"
        "completed_at"
        "exit_code"
        "metadata_gaps"
    ]

let private captureManifestDecoder : UTF8Encoding =
    UTF8Encoding(false, true)

let private captureManifestStartsWithBom (bytes: byte[]) : bool =
    bytes.Length >= 3
    && bytes.[0] = 0xEFuy
    && bytes.[1] = 0xBBuy
    && bytes.[2] = 0xBFuy

let private captureManifestContainsNul (bytes: byte[]) : bool =
    Array.exists (fun b -> b = 0uy) bytes

let private checkCaptureManifestDuplicates
    (path: string)
    (lineNumber: int)
    (root: JsonElement)
    : unit =
    let seen = HashSet<string>(StringComparer.Ordinal)
    let mutable dup : string option = None
    for prop in root.EnumerateObject() do
        if dup.IsNone && not (seen.Add prop.Name) then
            dup <- Some prop.Name
    match dup with
    | Some n -> captureManifestFail (DuplicateField (path, lineNumber, n))
    | None -> ()

let private checkObjectAgainst
    (knownFields: Set<string>)
    (path: string)
    (lineNumber: int)
    (root: JsonElement)
    : unit =
    let firstUnknown =
        root.EnumerateObject()
        |> Seq.tryPick (fun p ->
            if Set.contains p.Name knownFields then None
            else Some p.Name)
    match firstUnknown with
    | Some n -> captureManifestFail (UnknownField (path, lineNumber, n))
    | None -> ()

/// Known field set for a single ``SourceRootAlias`` object.  Distinct
/// from the top-level capture-manifest field set; an alias must contain
/// only ``absolute_root`` and ``canonical_root``.
let private sourceRootAliasKnownFields : Set<string> =
    set [ "absolute_root"; "canonical_root" ]

let private captureManifestRequiredString
    (path: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : string =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> captureManifestFail (MissingField (path, lineNumber, field))
    if el.ValueKind = JsonValueKind.Null then
        captureManifestFail (WrongJsonKind (path, lineNumber, field))
    elif el.ValueKind <> JsonValueKind.String then
        captureManifestFail (WrongJsonKind (path, lineNumber, field))
    else
        el.GetString()

let private captureManifestRequiredNullableString
    (path: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : string option =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> captureManifestFail (MissingField (path, lineNumber, field))
    if el.ValueKind = JsonValueKind.Null then
        None
    elif el.ValueKind <> JsonValueKind.String then
        captureManifestFail (WrongJsonKind (path, lineNumber, field))
    else
        Some (el.GetString())

let private captureManifestRequiredInt
    (path: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : int =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> captureManifestFail (MissingField (path, lineNumber, field))
    if el.ValueKind = JsonValueKind.Null then
        captureManifestFail (WrongJsonKind (path, lineNumber, field))
    elif el.ValueKind <> JsonValueKind.Number then
        captureManifestFail (WrongJsonKind (path, lineNumber, field))
    let raw = el.GetRawText()
    if raw.Contains "." || raw.Contains "e" || raw.Contains "E" then
        captureManifestFail (WrongJsonKind (path, lineNumber, field))
    try
        Int32.Parse(raw, CultureInfo.InvariantCulture)
    with _ ->
        captureManifestFail (WrongJsonKind (path, lineNumber, field))

let private captureManifestRequiredNullableInt
    (path: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : int option =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> captureManifestFail (MissingField (path, lineNumber, field))
    if el.ValueKind = JsonValueKind.Null then
        None
    elif el.ValueKind <> JsonValueKind.Number then
        captureManifestFail (WrongJsonKind (path, lineNumber, field))
    else
        let raw = el.GetRawText()
        if raw.Contains "." || raw.Contains "e" || raw.Contains "E" then
            captureManifestFail (WrongJsonKind (path, lineNumber, field))
        try
            Some (Int32.Parse(raw, CultureInfo.InvariantCulture))
        with _ ->
            captureManifestFail (WrongJsonKind (path, lineNumber, field))

let private captureManifestRequiredStringList
    (path: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : string list =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> captureManifestFail (MissingField (path, lineNumber, field))
    if el.ValueKind = JsonValueKind.Null then
        captureManifestFail (WrongJsonKind (path, lineNumber, field))
    elif el.ValueKind <> JsonValueKind.Array then
        captureManifestFail (WrongJsonKind (path, lineNumber, field))
    else
        let mutable acc : string list = []
        for item in el.EnumerateArray() do
            if item.ValueKind <> JsonValueKind.String then
                captureManifestFail (WrongJsonKind (path, lineNumber, field))
            else
                acc <- (item.GetString()) :: acc
        List.rev acc

let private captureManifestRequiredAliasList
    (path: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : SourceRootAlias list =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> captureManifestFail (MissingField (path, lineNumber, field))
    if el.ValueKind = JsonValueKind.Null then
        captureManifestFail (WrongJsonKind (path, lineNumber, field))
    elif el.ValueKind <> JsonValueKind.Array then
        captureManifestFail (WrongJsonKind (path, lineNumber, field))
    else
        let mutable acc : SourceRootAlias list = []
        for item in el.EnumerateArray() do
            if item.ValueKind <> JsonValueKind.Object then
                captureManifestFail (WrongJsonKind (path, lineNumber, field))
            checkCaptureManifestDuplicates path lineNumber item
            checkObjectAgainst sourceRootAliasKnownFields path lineNumber item
            let absoluteRoot =
                captureManifestRequiredString path lineNumber "absolute_root" item
            let canonicalRoot =
                captureManifestRequiredString path lineNumber "canonical_root" item
            acc <- { AbsoluteRoot = absoluteRoot; CanonicalRoot = canonicalRoot } :: acc
        List.rev acc

let private parseCaptureManifest
    (path: string)
    (lineNumber: int)
    (text: string)
    : CaptureManifest =
    let doc =
        try
            JsonDocument.Parse(text)
        with
        | :? JsonException as ex ->
            captureManifestFail (InvalidJson (path, lineNumber, ex.Message))

    use doc = doc
    let root = doc.RootElement
    if root.ValueKind <> JsonValueKind.Object then
        captureManifestFail (RootNotObject (path, lineNumber))
    checkCaptureManifestDuplicates path lineNumber root
    checkObjectAgainst captureManifestKnownFields path lineNumber root

    let schemaVersion = captureManifestRequiredString path lineNumber "schema_version" root
    if schemaVersion <> CaptureManifestSchemaVersion then
        captureManifestFail (SchemaVersionMismatch (path, lineNumber, schemaVersion))
    let captureId = captureManifestRequiredString path lineNumber "capture_id" root
    let captureKind = captureManifestRequiredString path lineNumber "capture_kind" root
    let rawArtifacts = captureManifestRequiredStringList path lineNumber "raw_artifacts" root
    let command = captureManifestRequiredNullableString path lineNumber "command" root
    let workingDirectory = captureManifestRequiredNullableString path lineNumber "working_directory" root
    let repositoryCommitOid = captureManifestRequiredNullableString path lineNumber "repository_commit_oid" root
    let repositoryTreeOid = captureManifestRequiredNullableString path lineNumber "repository_tree_oid" root
    let workingTreeState = captureManifestRequiredNullableString path lineNumber "working_tree_state" root
    let sourceRootAliases = captureManifestRequiredAliasList path lineNumber "source_root_aliases" root
    let dotnetSdkVersion = captureManifestRequiredNullableString path lineNumber "dotnet_sdk_version" root
    let msbuildVersion = captureManifestRequiredNullableString path lineNumber "msbuild_version" root
    let fsharpCompilerVersion = captureManifestRequiredNullableString path lineNumber "fsharp_compiler_version" root
    let operatingSystem = captureManifestRequiredNullableString path lineNumber "operating_system" root
    let architecture = captureManifestRequiredNullableString path lineNumber "architecture" root
    let culture = captureManifestRequiredNullableString path lineNumber "culture" root
    let startedAt = captureManifestRequiredNullableString path lineNumber "started_at" root
    let completedAt = captureManifestRequiredNullableString path lineNumber "completed_at" root
    let exitCode = captureManifestRequiredNullableInt path lineNumber "exit_code" root
    let metadataGaps = captureManifestRequiredStringList path lineNumber "metadata_gaps" root

    { SchemaVersion = schemaVersion
      CaptureId = captureId
      CaptureKind = captureKind
      RawArtifacts = rawArtifacts
      Command = command
      WorkingDirectory = workingDirectory
      RepositoryCommitOid = repositoryCommitOid
      RepositoryTreeOid = repositoryTreeOid
      WorkingTreeState = workingTreeState
      SourceRootAliases = sourceRootAliases
      DotnetSdkVersion = dotnetSdkVersion
      MsbuildVersion = msbuildVersion
      FsharpCompilerVersion = fsharpCompilerVersion
      OperatingSystem = operatingSystem
      Architecture = architecture
      Culture = culture
      StartedAt = startedAt
      CompletedAt = completedAt
      ExitCode = exitCode
      MetadataGaps = metadataGaps }

let private readCaptureManifestStrict
    (path: string)
    : Result<CaptureManifest, CaptureManifestReadFailure> =
    if not (File.Exists path) then
        Result.Error (FileMissing path)
    else
        let bytesResult =
            try
                Result.Ok (File.ReadAllBytes path)
            with
            | :? IOException as ex -> Result.Error (FileReadFailed (path, ex.Message))
            | :? UnauthorizedAccessException as ex -> Result.Error (FileReadFailed (path, ex.Message))
        match bytesResult with
        | Result.Error e -> Result.Error e
        | Result.Ok bytes ->
            if captureManifestStartsWithBom bytes then
                Result.Error (BomPresent path)
            elif captureManifestContainsNul bytes then
                Result.Error (NulBytePresent path)
            else
                let text =
                    try
                        Result.Ok (captureManifestDecoder.GetString bytes)
                    with
                    | :? DecoderFallbackException as ex ->
                        Result.Error (InvalidByteSequence (path, ex.Message))
                match text with
                | Result.Error e -> Result.Error e
                | Result.Ok text ->
                    try
                        Result.Ok (parseCaptureManifest path 1 text)
                    with
                    | CaptureManifestParseAbort f -> Result.Error f

/// Strict capture-manifest reader.  Exposed for tests and for callers that
/// need the typed result directly.  The public ``bindCapture`` entry point
/// wraps this reader with the binding's failure union.  Marked ``internal``
/// to satisfy the F# accessibility rules around ``CaptureManifestReadFailure``.
let internal readCaptureManifest
    (path: string)
    : Result<CaptureManifest, CaptureManifestReadFailure> =
    readCaptureManifestStrict path

// =============================================================================
// Raw artifact file reading
// =============================================================================

let private hashAndLength (path: string) : Result<int64 * string, CaptureBindingFailure> =
    try
        use stream = File.OpenRead(path)
        let hash = SHA256.HashData(stream)
        let length = stream.Length
        let sb = StringBuilder(hash.Length * 2)
        for b in hash do
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture)) |> ignore
        Result.Ok (length, sb.ToString())
    with
    | :? IOException as ex -> Result.Error (RawArtifactReadFailed (path, ex.Message))
    | :? UnauthorizedAccessException as ex ->
        Result.Error (RawArtifactReadFailed (path, ex.Message))

let private bindRawArtifact
    (captureId: string)
    (rawPath: string)
    (manifest: ArtifactManifestEntry list)
    (seenCanonicalPaths: HashSet<string>)
    (repoRoot: string)
    : Result<BoundRawArtifact, CaptureBindingFailure> =
    if not (isValidRawArtifactPath rawPath) then
        Result.Error (RawArtifactPathInvalid rawPath)
    else
        let canonical =
            canonicalise (captureCanonicalDir captureId + "/" + rawPath)
        if not (seenCanonicalPaths.Add canonical) then
            Result.Error (DuplicateRawArtifactPath canonical)
        else
            let fullPath = repoRelative repoRoot canonical
            if not (File.Exists fullPath) then
                Result.Error (RawArtifactMissing canonical)
            else
                try
                    // Reject symbolic links and junctions at the file and at
                    // any intermediate directory between the file and the
                    // capture directory.  An inability to inspect a
                    // directory's reparse-point status is propagated as
                    // ``PathInspectionFailed`` and surfaces as
                    // ``RawArtifactReadFailed`` so the binding cannot be
                    // silently tricked into reading a redirected file.
                    let captureAbsDir =
                        Path.GetFullPath(Path.Combine(repoRoot, captureCanonicalDir captureId))
                    let fileParent = Path.GetDirectoryName fullPath
                    match findReparsePointBetween fileParent captureAbsDir with
                    | Some _ -> Result.Error (RawArtifactPathInvalid canonical)
                    | None ->
                        if isReparsePoint fullPath then
                            Result.Error (RawArtifactPathInvalid canonical)
                        else
                            match hashAndLength fullPath with
                            | Result.Error e -> Result.Error e
                            | Result.Ok (actualLength, actualHash) ->
                                let matches =
                                    manifest
                                    |> List.filter (fun e -> e.CanonicalPath = canonical)
                                match matches with
                                | [] -> Result.Error (ArtifactManifestEntryMissing canonical)
                                | _ :: _ :: _ ->
                                    Result.Error (ArtifactManifestEntryDuplicate canonical)
                                | [ entry ] ->
                                    if entry.CaptureId <> Some captureId then
                                        Result.Error (ArtifactManifestCaptureMismatch canonical)
                                    elif entry.ArtifactClass <> rawArtifactCanonicalClass then
                                        Result.Error (ArtifactManifestClassMismatch canonical)
                                    elif entry.Status <> presentCanonicalStatus then
                                        Result.Error (ArtifactManifestStatusMismatch canonical)
                                    elif entry.ByteLength <> actualLength then
                                        Result.Error
                                            (RawArtifactLengthMismatch (canonical, entry.ByteLength, actualLength))
                                    elif not (String.Equals(entry.Sha256, actualHash, StringComparison.Ordinal)) then
                                        Result.Error
                                            (RawArtifactHashMismatch (canonical, entry.Sha256, actualHash))
                                    else
                                        Result.Ok
                                            { CanonicalPath = canonical
                                              ByteLength = actualLength
                                              Sha256 = actualHash }
                with
                | PathInspectionFailed msg ->
                    Result.Error (RawArtifactReadFailed (canonical, "reparse-point inspection failed: " + msg))

// =============================================================================
// Capture binding entry point
// =============================================================================

let bindCapture
    (repoRoot: string)
    (request: CaptureBindingRequest)
    : Result<BoundCapture, CaptureBindingFailure> =
    // 1. Validate the requested capture ID before touching the filesystem.
    if not (isValidCaptureId request.CaptureId) then
        Result.Error (InvalidCaptureId request.CaptureId)
    else
        let captureRelDir = captureCanonicalDir request.CaptureId
        let captureManifestPath = repoRelative repoRoot (captureRelDir + "/capture.json")
        let captureManifestCanonical = captureRelDir + "/capture.json"

        // 2. Locate the canonical capture manifest.
        match readCaptureManifestStrict captureManifestPath with
        | Result.Error (FileMissing _) ->
            Result.Error (CaptureManifestMissing captureManifestCanonical)
        | Result.Error (FileReadFailed (_, detail)) ->
            Result.Error (CaptureManifestReadFailed (captureManifestCanonical, detail))
        | Result.Error (InvalidByteSequence (_, detail)) ->
            Result.Error (CaptureManifestReadFailed (captureManifestCanonical, "invalid UTF-8: " + detail))
        | Result.Error (BomPresent _) ->
            Result.Error (CaptureManifestReadFailed (captureManifestCanonical, "UTF-8 BOM is not canonical"))
        | Result.Error (NulBytePresent _) ->
            Result.Error (CaptureManifestReadFailed (captureManifestCanonical, "NUL byte is not canonical"))
        | Result.Error (InvalidJson (_, line, detail)) ->
            Result.Error (CaptureManifestReadFailed (captureManifestCanonical, sprintf "line %d: %s" line detail))
        | Result.Error (RootNotObject (_, line)) ->
            Result.Error (CaptureManifestReadFailed (captureManifestCanonical, sprintf "line %d: root is not an object" line))
        | Result.Error (MissingField (_, line, field)) ->
            Result.Error (CaptureManifestReadFailed (captureManifestCanonical, sprintf "line %d: missing field '%s'" line field))
        | Result.Error (DuplicateField (_, line, field)) ->
            Result.Error (CaptureManifestReadFailed (captureManifestCanonical, sprintf "line %d: duplicate field '%s'" line field))
        | Result.Error (UnknownField (_, line, field)) ->
            Result.Error (CaptureManifestReadFailed (captureManifestCanonical, sprintf "line %d: unknown field '%s'" line field))
        | Result.Error (WrongJsonKind (_, line, field)) ->
            Result.Error (CaptureManifestReadFailed (captureManifestCanonical, sprintf "line %d: wrong JSON kind for '%s'" line field))
        | Result.Error (SchemaVersionMismatch (_, line, actual)) ->
            Result.Error (CaptureManifestReadFailed (captureManifestCanonical, sprintf "line %d: schema_version mismatch '%s'" line actual))
        | Result.Ok manifest ->
            // 3. The manifest's capture_id must equal the requested capture_id.
            if manifest.CaptureId <> request.CaptureId then
                Result.Error (CaptureIdMismatch (request.CaptureId, manifest.CaptureId))
            else
                // 4. The manifest must record a repository commit OID equal to
                //    the resolution supplied by the caller.
                match manifest.RepositoryCommitOid with
                | None -> Result.Error (RepositoryCommitOidMissing request.CaptureId)
                | Some recorded when recorded <> request.ResolvedCommitOid ->
                    Result.Error (RepositoryCommitOidMismatch (request.ResolvedCommitOid, recorded))
                | Some _ ->
                    // 5. The manifest must record a repository tree OID equal to
                    //    the resolution supplied by the caller.
                    match manifest.RepositoryTreeOid with
                    | None -> Result.Error (RepositoryTreeOidMissing request.CaptureId)
                    | Some recorded when recorded <> request.ResolvedTreeOid ->
                        Result.Error (RepositoryTreeOidMismatch (request.ResolvedTreeOid, recorded))
                    | Some _ ->
                        // 6. Optional expected-tree assertion.
                        match request.ExpectedTreeOid with
                        | Some expected when expected <> request.ResolvedTreeOid ->
                            Result.Error (ExpectedTreeOidMismatch (expected, request.ResolvedTreeOid))
                        | _ ->
                            // 7. Raw artifact list must be non-empty.
                            if List.isEmpty manifest.RawArtifacts then
                                Result.Error (RawArtifactListEmpty request.CaptureId)
                            else
                                // 8. Locate the canonical artifact manifest.
                                let artifactManifestPath =
                                    repoRelative repoRoot artifactsManifestCanonicalPath
                                match readArtifactManifestStrict artifactManifestPath with
                                | Result.Error (ArtifactManifestReadFailure.FileMissing _) ->
                                    Result.Error (ArtifactManifestMissing artifactsManifestCanonicalPath)
                                | Result.Error (ArtifactManifestReadFailure.FileReadFailed (_, detail)) ->
                                    Result.Error (ArtifactManifestReadFailed (artifactsManifestCanonicalPath, 0, detail))
                                | Result.Error (ArtifactManifestReadFailure.InvalidByteSequence (_, detail)) ->
                                    Result.Error (ArtifactManifestReadFailed (artifactsManifestCanonicalPath, 0, "invalid UTF-8: " + detail))
                                | Result.Error (ArtifactManifestReadFailure.BomPresent _) ->
                                    Result.Error (ArtifactManifestReadFailed (artifactsManifestCanonicalPath, 0, "UTF-8 BOM is not canonical"))
                                | Result.Error (ArtifactManifestReadFailure.NulBytePresent _) ->
                                    Result.Error (ArtifactManifestReadFailed (artifactsManifestCanonicalPath, 0, "NUL byte is not canonical"))
                                | Result.Error (ArtifactManifestReadFailure.InvalidJson (_, line, detail)) ->
                                    Result.Error (ArtifactManifestReadFailed (artifactsManifestCanonicalPath, line, detail))
                                | Result.Error (ArtifactManifestReadFailure.RootNotObject (_, line)) ->
                                    Result.Error (ArtifactManifestReadFailed (artifactsManifestCanonicalPath, line, "root is not an object"))
                                | Result.Error (ArtifactManifestReadFailure.MissingField (_, line, field)) ->
                                    Result.Error (ArtifactManifestReadFailed (artifactsManifestCanonicalPath, line, "missing field: " + field))
                                | Result.Error (ArtifactManifestReadFailure.DuplicateField (_, line, field)) ->
                                    Result.Error (ArtifactManifestReadFailed (artifactsManifestCanonicalPath, line, "duplicate field: " + field))
                                | Result.Error (ArtifactManifestReadFailure.UnknownField (_, line, field)) ->
                                    Result.Error (ArtifactManifestReadFailed (artifactsManifestCanonicalPath, line, "unknown field: " + field))
                                | Result.Error (ArtifactManifestReadFailure.WrongJsonKind (_, line, field)) ->
                                    Result.Error (ArtifactManifestReadFailed (artifactsManifestCanonicalPath, line, "wrong json kind: " + field))
                                | Result.Error (ArtifactManifestReadFailure.SchemaVersionMismatch (_, line, actual)) ->
                                    Result.Error (ArtifactManifestReadFailed (artifactsManifestCanonicalPath, line, "schema_version mismatch: " + actual))
                                | Result.Ok artifactEntries ->
                                    // 9. Bind every declared raw artifact.
                                    let seen = HashSet<string>(StringComparer.Ordinal)
                                    let mutable boundList : BoundRawArtifact list = []
                                    let mutable boundFailure : CaptureBindingFailure =
                                        RawArtifactListEmpty request.CaptureId
                                    let mutable aborted = false
                                    for rawPath in manifest.RawArtifacts do
                                        if not aborted then
                                            match bindRawArtifact request.CaptureId rawPath artifactEntries seen repoRoot with
                                            | Result.Ok bound ->
                                                boundList <- bound :: boundList
                                            | Result.Error f ->
                                                aborted <- true
                                                boundFailure <- f
                                    if aborted then
                                        Result.Error boundFailure
                                    else
                                        // 10. Read the canonical occurrence stream.
                                        let occurrencesPath =
                                            repoRelative repoRoot occurrencesCanonicalPath
                                        match readOccurrences occurrencesPath with
                                        | Result.Error e ->
                                            Result.Error (OccurrenceStreamFailure e)
                                        | Result.Ok allOccurrences ->
                                            // 11. Select occurrences for this capture and
                                            //     reject duplicate event ordinals.
                                            let filtered =
                                                allOccurrences
                                                |> List.filter (fun o -> o.CaptureId = request.CaptureId)
                                            let seenOrdinals = HashSet<int64>()
                                            let mutable dupOrdinal : int64 option = None
                                            for occ in filtered do
                                                if dupOrdinal.IsNone && not (seenOrdinals.Add occ.EventOrdinal) then
                                                    dupOrdinal <- Some occ.EventOrdinal
                                            match dupOrdinal with
                                            | Some n ->
                                                Result.Error (DuplicateOccurrenceOrdinal (request.CaptureId, n))
                                            | None ->
                                                let sorted =
                                                    boundList
                                                    |> List.sortBy (fun b -> b.CanonicalPath)
                                                Result.Ok
                                                    { Manifest = manifest
                                                      Occurrences = filtered
                                                      RawArtifacts = sorted }
