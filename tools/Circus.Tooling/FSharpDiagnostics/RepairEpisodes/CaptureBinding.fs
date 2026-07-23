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
//   * it does not distinguish absent and null for required fields.
//
// This private reader implements the strict contract required by the
// binding: malformed nonblank records abort the entire file, the one-based
// line number is preserved, duplicate and unknown properties are rejected,
// and absent and null are distinguished for every required field.

// ``ArtifactManifestReadFailure`` is internal to this module. The type is
// declared without an explicit accessibility modifier so the private
// exception used to short-circuit the parser can carry it as a payload
// without violating F# accessibility rules. The type is not re-exported:
// every bound value that produces it is private and the only public
// surface is ``Result<ArtifactManifestEntry list, ArtifactManifestReadFailure>``.
type internal ArtifactManifestReadFailure =
    | FileMissing of canonicalPath: string
    | FileReadFailed of canonicalPath: string * detail: string
    | InvalidJson of canonicalPath: string * lineNumber: int * detail: string
    | RootNotObject of canonicalPath: string * lineNumber: int
    | MissingField of canonicalPath: string * lineNumber: int * field: string
    | DuplicateField of canonicalPath: string * lineNumber: int * field: string
    | UnknownField of canonicalPath: string * lineNumber: int * field: string
    | WrongJsonKind of canonicalPath: string * lineNumber: int * field: string

exception private ArtifactManifestParseAbort of ArtifactManifestReadFailure
do ()

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
            let text =
                try
                    Result.Ok (Encoding.UTF8.GetString bytes)
                with
                | :? DecoderFallbackException as ex ->
                    Result.Error (FileReadFailed (path, ex.Message))
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
// Strict capture manifest read
// =============================================================================
//
// The foundation ``Manifest.readCaptureManifest`` raises on failure without
// preserving the original cause.  This wrapper traps the failure types we
// need to surface as ``CaptureManifestReadFailed`` and treats missing files
// as the dedicated ``CaptureManifestMissing`` failure.

let private readCaptureManifestStrict
    (path: string)
    : Result<CaptureManifest, CaptureBindingFailure> =
    if not (File.Exists path) then
        Result.Error (CaptureManifestMissing path)
    else
        try
            Result.Ok (Circus.Tooling.FSharpDiagnostics.Manifest.readCaptureManifest path)
        with
        | ex -> Result.Error (CaptureManifestReadFailed (path, ex.Message))

// =============================================================================
// Raw artifact binding
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

let private readFileAttributes
    (path: string)
    : Result<FileAttributes, CaptureBindingFailure> =
    try
        Result.Ok (File.GetAttributes(path))
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
                Result.Error (RawArtifactMissing fullPath)
            else
                match readFileAttributes fullPath with
                | Result.Error e -> Result.Error e
                | Result.Ok attrs ->
                    if (attrs &&& FileAttributes.Directory) <> FileAttributes.None then
                        Result.Error (RawArtifactPathInvalid rawPath)
                    else
                        match hashAndLength fullPath with
                        | Result.Error e -> Result.Error e
                        | Result.Ok (actualLength, actualHash) ->
                            let matches =
                                manifest
                                |> List.filter (fun e -> e.CanonicalPath = canonical)
                            match matches with
                            | [] -> Result.Error (ArtifactManifestEntryMissing fullPath)
                            | _ :: _ :: _ ->
                                Result.Error (ArtifactManifestEntryDuplicate fullPath)
                            | [ entry ] ->
                                if entry.CaptureId <> Some captureId then
                                    Result.Error (ArtifactManifestCaptureMismatch fullPath)
                                elif entry.ArtifactClass <> rawArtifactCanonicalClass then
                                    Result.Error (ArtifactManifestClassMismatch fullPath)
                                elif entry.Status <> presentCanonicalStatus then
                                    Result.Error (ArtifactManifestStatusMismatch fullPath)
                                elif entry.ByteLength <> actualLength then
                                    Result.Error
                                        (RawArtifactLengthMismatch (fullPath, entry.ByteLength, actualLength))
                                elif not (String.Equals(entry.Sha256, actualHash, StringComparison.Ordinal)) then
                                    Result.Error
                                        (RawArtifactHashMismatch (fullPath, entry.Sha256, actualHash))
                                else
                                    Result.Ok
                                        { CanonicalPath = fullPath
                                          ByteLength = actualLength
                                          Sha256 = actualHash }

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

        // 2. Locate the canonical capture manifest.
        match readCaptureManifestStrict captureManifestPath with
        | Result.Error e -> Result.Error e
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
                                | Result.Error (FileMissing p) ->
                                    Result.Error (ArtifactManifestMissing p)
                                | Result.Error (FileReadFailed (p, detail)) ->
                                    Result.Error (ArtifactManifestReadFailed (p, 0, detail))
                                | Result.Error (InvalidJson (p, line, detail)) ->
                                    Result.Error (ArtifactManifestReadFailed (p, line, detail))
                                | Result.Error (RootNotObject (p, line)) ->
                                    Result.Error (ArtifactManifestReadFailed (p, line, "root is not an object"))
                                | Result.Error (MissingField (p, line, field)) ->
                                    Result.Error (ArtifactManifestReadFailed (p, line, "missing field: " + field))
                                | Result.Error (DuplicateField (p, line, field)) ->
                                    Result.Error (ArtifactManifestReadFailed (p, line, "duplicate field: " + field))
                                | Result.Error (UnknownField (p, line, field)) ->
                                    Result.Error (ArtifactManifestReadFailed (p, line, "unknown field: " + field))
                                | Result.Error (WrongJsonKind (p, line, field)) ->
                                    Result.Error (ArtifactManifestReadFailed (p, line, "wrong json kind: " + field))
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
