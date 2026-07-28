module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine

open System

open System.IO
open System.Text
open Circus.Tooling.FSharpDiagnostics.AtomicPublish
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Inventory
open Circus.Tooling.FSharpDiagnostics.LegacyTextExtractor
open Circus.Tooling.FSharpDiagnostics.Manifest
open Circus.Tooling.FSharpDiagnostics.OccurrenceIdentity
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Episodes
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Serialization
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Transitions
open Circus.Tooling.FSharpDiagnostics.Verifier

type EpisodeEngineOptions = {
    GitRunOptions: GitRunOptions
}

let defaultEngineOptions : EpisodeEngineOptions =
    { GitRunOptions = defaultGitRunOptions }

let private lookupString (fields: (string * JsonValue) list) (name: string) : string option =
    fields
    |> List.tryPick (fun (k, v) ->
        if k = name then
            match v with
            | JsonString s -> Some s
            | _ -> None
        else None)

let private lookupOptString (fields: (string * JsonValue) list) (name: string) : string option =
    fields
    |> List.tryPick (fun (k, v) ->
        if k = name then
            match v with
            | JsonString s -> Some s
            | JsonNull -> None
            | _ -> None
        else None)

let private lookupStringList (fields: (string * JsonValue) list) (name: string) : string list =
    fields
    |> List.tryPick (fun (k, v) ->
        if k = name then
            match v with
            | JsonArray items ->
                Some (items |> List.choose (function JsonString s -> Some s | _ -> None))
            | _ -> None
        else None)
    |> Option.defaultValue []

let private lookupInt (fields: (string * JsonValue) list) (name: string) : int option =
    fields
    |> List.tryPick (fun (k, v) ->
        if k = name then
            match v with
            | JsonNumber n -> Some (int n)
            | _ -> None
        else None)


// =============================================================================
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION08
// Workstream 2: Type-aware JSON lookup with FieldLookup discriminated union
// Workstream 11: Commit geometry
// =============================================================================

/// Typed field lookup result for JSON parsing.
/// Distinguishes between absent field, wrong JSON type, and valid value.
type FieldLookup<'value> =
    | Missing
    | WrongType of expectedType: string * actualType: string
    | Present of 'value

/// Get the JSON type name for error reporting.
let private jsonTypeName (v: JsonValue) : string =
    match v with
    | JsonString _ -> "string"
    | JsonNumber _ -> "number"
    | JsonBool _ -> "boolean"
    | JsonNull -> "null"
    | JsonArray _ -> "array"
    | JsonObject _ -> "object"

/// Type-aware string field lookup that distinguishes Missing vs WrongType.
let private lookupFieldString (fields: (string * JsonValue) list) (name: string) : FieldLookup<string> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Missing
    | Some (_, v) ->
        match v with
        | JsonString s -> Present s
        | _ -> WrongType ("string", jsonTypeName v)

/// Type-aware optional string field lookup.
let private lookupFieldOptString (fields: (string * JsonValue) list) (name: string) : FieldLookup<string option> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Missing
    | Some (_, v) ->
        match v with
        | JsonNull -> Present None
        | JsonString s -> Present (Some s)
        | _ -> WrongType ("string", jsonTypeName v)

/// Type-aware integer field lookup with strict validation.
/// Rejects fractional numbers, values below Int32.MinValue, above Int32.MaxValue.
let private lookupFieldInt (fields: (string * JsonValue) list) (name: string) : FieldLookup<int> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Missing
    | Some (_, v) ->
        match v with
        | JsonNumber n ->
            let d = double n
            let vi = int n
            if d <> float vi then
                WrongType ("integer (fractional)", jsonTypeName v)
            elif vi < System.Int32.MinValue then
                WrongType ("integer (underflow)", jsonTypeName v)
            elif vi > System.Int32.MaxValue then
                WrongType ("integer (overflow)", jsonTypeName v)
            else
                Present vi
        | _ -> WrongType ("integer", jsonTypeName v)


// =============================================================================
// Workstream 3: LocatedVerificationEvidence with source locations
// =============================================================================

/// Verification evidence with source location information for error reporting.
type LocatedVerificationEvidence = {
    Evidence: VerificationEvidence
    SourcePath: string
    SourceLine: int
}

/// Convert a FieldLookup result to a verification parse error if the field is absent or wrong type.
let private fieldToError (source: string) (lineNumber: int) (fieldName: string) (lookup: FieldLookup<'a>) : VerificationEvidenceParseError option =
    match lookup with
    | Missing -> Some (VerificationEvidenceParseError.MissingField(source, lineNumber, fieldName))
    | WrongType (_, actualType) -> Some (VerificationEvidenceParseError.WrongFieldType(source, lineNumber, fieldName, actualType))
    | Present _ -> None

// =============================================================================
// Workstream 7: Commit geometry fail-closed with Result type
// =============================================================================

/// Error types for commit geometry resolution.
type CommitGeometryError =
    | RepositoryNotFound of path: string
    | GitFailure of detail: string
    | DirtyWorktree
    | UnspecifiedHead

/// Commit geometry records the Git OID bindings for a verification run.
type CommitGeometry = {
    /// The commit being verified (subject of the repair episode).
    SubjectCommitOid: string
    /// The tree OID of the subject commit.
    SubjectTreeOid: string
    /// The commit that recorded the evidence (may differ from subject).
    EvidenceCommitOid: string option
    /// The commit that recorded the canonical closure (final artifact).
    ClosureCommitOid: string option
}

/// Resolve commit geometry from a repository with fail-closed semantics.
/// Returns Result.Error if HEAD is unspecified or repository state is invalid.
/// Does NOT resolve unspecified HEAD - fails closed.
let resolveCommitGeometry (repoRoot: string) : Result<CommitGeometry, CommitGeometryError> =
    if String.IsNullOrWhiteSpace repoRoot then
        Result.Error(CommitGeometryError.RepositoryNotFound repoRoot)
    elif not (Directory.Exists repoRoot) then
        Result.Error(CommitGeometryError.RepositoryNotFound repoRoot)
    else
        // Step 1: Check for dirty worktree first (fail-closed)
        match runGitTyped repoRoot defaultGitRunOptions [ "status"; "--porcelain=v1" ] with
        | Ok statusRun when statusRun.ExitCode = 0 && String.IsNullOrEmpty(statusRun.Stdout.Trim()) ->
            // Worktree is clean, continue to resolve HEAD
            // Step 2: Resolve HEAD commit
            match runGitTyped repoRoot defaultGitRunOptions [ "rev-parse"; "--verify"; "--end-of-options"; "HEAD^{commit}" ] with
            | Ok headRun when headRun.ExitCode = 0 ->
                let headCommit = headRun.Stdout.Trim()
                if String.IsNullOrEmpty headCommit then
                    Result.Error CommitGeometryError.UnspecifiedHead
                else
                    // Step 3: Resolve HEAD tree
                    match runGitTyped repoRoot defaultGitRunOptions [ "rev-parse"; "--verify"; "--end-of-options"; "HEAD^{tree}" ] with
                    | Ok treeRun when treeRun.ExitCode = 0 ->
                        let treeOid = treeRun.Stdout.Trim()
                        Result.Ok {
                            SubjectCommitOid = headCommit
                            SubjectTreeOid = treeOid
                            EvidenceCommitOid = None
                            ClosureCommitOid = None
                        }
                    | Ok _ ->
                        Result.Error(CommitGeometryError.GitFailure "HEAD tree resolution failed")
                    | Result.Error other ->
                        Result.Error(CommitGeometryError.GitFailure(sprintf "HEAD tree: %A" other))
            | Ok _ ->
                // Exit code non-zero but no error thrown - HEAD is unspecified
                Result.Error CommitGeometryError.UnspecifiedHead
            | Result.Error (GitRunError.ExitFailure _) ->
                // Non-zero exit from rev-parse means HEAD is unspecified
                Result.Error CommitGeometryError.UnspecifiedHead
            | Result.Error other ->
                Result.Error(CommitGeometryError.GitFailure(sprintf "HEAD commit: %A" other))
        | Ok _ ->
            // Worktree has changes
            Result.Error CommitGeometryError.DirtyWorktree
        | Result.Error (GitRunError.ExitFailure _) ->
            Result.Error(CommitGeometryError.GitFailure "status check failed")
        | Result.Error other ->
            Result.Error(CommitGeometryError.GitFailure(sprintf "git error: %A" other))

/// Legacy version for backward compatibility - wraps Result in a default value.
let resolveCommitGeometryLegacy (repoRoot: string) : CommitGeometry =
    match resolveCommitGeometry repoRoot with
    | Result.Ok geo -> geo
    | Result.Error _ ->
        { SubjectCommitOid = ""
          SubjectTreeOid = ""
          EvidenceCommitOid = None
          ClosureCommitOid = None }

/// Strict regex patterns for validation.
open System.Text.RegularExpressions

let private sha256Regex = Regex(@"^[a-f0-9]{64}$", RegexOptions.Compiled)
let private oid40Regex = Regex(@"^[a-f0-9]{40}$", RegexOptions.Compiled)
let private oid64Regex = Regex(@"^[a-f0-9]{64}$", RegexOptions.Compiled)
let private placeholderIdRegex = Regex(@"^[0]+$", RegexOptions.Compiled)

/// Helper to construct VerificationEvidence record after all validations pass.
let private buildVerificationEvidenceRecord
        (evId: string)
        (epId: string)
        (parsedKind: VerificationKind)
        (cmd: string)
        (parsedStatus: VerificationStatus)
        (testedCommitOid: string)
        (testedTreeOid: string)
        (ec: int)
        (stdoutSha: string option)
        (stderrSha: string option)
        : VerificationEvidence =
    { SchemaVersion = VerificationEvidenceSchemaVersion
      EvidenceId = evId
      EpisodeId = epId
      Kind = parsedKind
      Command = cmd
      WorkingDirectory = ""
      TestedCommitOid = testedCommitOid
      TestedTreeOid = testedTreeOid
      ExitCode = ec
      StdoutSha256 = stdoutSha
      StderrSha256 = stderrSha
      CombinedLogPath = None
      Status = parsedStatus }

/// Parse a verification evidence record from JSON with strict validation.
/// Returns Result to preserve exact failure information.
let private parseVerificationEvidenceStrict (json: string) (source: string) (lineNumber: int) : Result<VerificationEvidence, VerificationEvidenceParseError> =
    try
        let v = parseJson json
        match v with
        | JsonObject fields ->
            // Validate schema version first
            match lookupOptString fields "schema_version" with
            | Some sv when sv <> VerificationEvidenceSchemaVersion ->
                Result.Error (VerificationEvidenceParseError.UnsupportedSchemaVersion(source, lineNumber, sv))
            | _ ->
                // Get required fields
                match lookupString fields "verification_evidence_id" with
                | None -> Result.Error (VerificationEvidenceParseError.MissingField(source, lineNumber, "verification_evidence_id"))
                | Some evId ->
                    // Validate evidence ID format
                    if not (sha256Regex.IsMatch(evId)) then
                        Result.Error (VerificationEvidenceParseError.InvalidEvidenceId(source, lineNumber, evId))
                    elif placeholderIdRegex.IsMatch(evId) then
                        Result.Error (VerificationEvidenceParseError.PlaceholderEvidenceId(source, lineNumber, evId))
                    else
                        match lookupString fields "episode_id" with
                        | None -> Result.Error (VerificationEvidenceParseError.MissingField(source, lineNumber, "episode_id"))
                        | Some epId ->
                            match lookupString fields "verification_kind" with
                            | None -> Result.Error (VerificationEvidenceParseError.MissingField(source, lineNumber, "verification_kind"))
                            | Some kindToken ->
                                match tryParseVerificationKind kindToken with
                                | None -> Result.Error (VerificationEvidenceParseError.UnknownVerificationKind(source, lineNumber, kindToken))
                                | Some parsedKind ->
                                    match lookupString fields "verification_command" with
                                    | None -> Result.Error (VerificationEvidenceParseError.MissingField(source, lineNumber, "verification_command"))
                                    | Some cmd ->
                                        match lookupString fields "verification_result" with
                                        | None -> Result.Error (VerificationEvidenceParseError.MissingField(source, lineNumber, "verification_result"))
                                        | Some statusToken ->
                                            match tryParseVerificationStatus statusToken with
                                            | None -> Result.Error (VerificationEvidenceParseError.UnknownVerificationStatus(source, lineNumber, statusToken))
                                            | Some parsedStatus ->
                                                // Exit code is required and must be non-negative
                                                match lookupInt fields "verification_exit_code" with
                                                | None -> Result.Error (VerificationEvidenceParseError.InvalidExitCode(source, lineNumber, "null"))
                                                | Some ec when ec < 0 -> Result.Error (VerificationEvidenceParseError.InvalidExitCode(source, lineNumber, string ec))
                                                | Some ec ->
                                                    // Validate commit OID if present
                                                    let testedCommitOid = lookupOptString fields "tested_commit_oid" |> Option.defaultValue ""
                                                    if testedCommitOid.Length > 0 && not (oid40Regex.IsMatch(testedCommitOid) || oid64Regex.IsMatch(testedCommitOid)) then
                                                        Result.Error (VerificationEvidenceParseError.InvalidCommitOid(source, lineNumber, "tested_commit_oid", testedCommitOid))
                                                    else
                                                        // Validate tree OID if present
                                                        let testedTreeOid = lookupOptString fields "tested_tree_oid" |> Option.defaultValue ""
                                                        if testedTreeOid.Length > 0 && not (oid40Regex.IsMatch(testedTreeOid) || oid64Regex.IsMatch(testedTreeOid)) then
                                                            Result.Error (VerificationEvidenceParseError.InvalidTreeOid(source, lineNumber, "tested_tree_oid", testedTreeOid))
                                                        else
                                                            // Validate SHA-256 fields if present
                                                            let stdoutSha = lookupOptString fields "stdout_sha256"
                                                            match stdoutSha with
                                                            | Some v when not (sha256Regex.IsMatch(v)) ->
                                                                Result.Error (VerificationEvidenceParseError.InvalidSha256(source, lineNumber, "stdout_sha256", v))
                                                            | _ ->
                                                                let stderrSha = lookupOptString fields "stderr_sha256"
                                                                match stderrSha with
                                                                | Some v when not (sha256Regex.IsMatch(v)) ->
                                                                    Result.Error (VerificationEvidenceParseError.InvalidSha256(source, lineNumber, "stderr_sha256", v))
                                                                | _ ->
                                                                    let wd = lookupOptString fields "working_directory" |> Option.defaultValue ""
                                                                    let logPath = lookupOptString fields "combined_log_path"
                                                                    let record = buildVerificationEvidenceRecord evId epId parsedKind cmd parsedStatus testedCommitOid testedTreeOid ec stdoutSha stderrSha
                                                                    Result.Ok { record with WorkingDirectory = wd; CombinedLogPath = logPath }
        | _ -> Result.Error (VerificationEvidenceParseError.ExpectedObject(source, lineNumber))
    with
    | JsonParseException (_, msg) ->
        Result.Error (VerificationEvidenceParseError.MalformedJson(source, lineNumber, msg))
    | :? System.Text.Json.JsonException as ex ->
        Result.Error (VerificationEvidenceParseError.JsonException(source, lineNumber, ex.Message))
    | ex ->
        Result.Error (VerificationEvidenceParseError.JsonException(source, lineNumber, ex.Message))

/// Load verification evidence with strict all-or-nothing semantics.
/// - Missing file fails
/// - Unreadable file fails
/// - One malformed line fails the whole load
/// - Duplicate IDs fail
/// - Conflicting records fail
let loadVerificationEvidenceStrict (repoRoot: string) : Result<VerificationEvidence list, VerificationEvidenceLoadError list> =
    let path = repoRelative repoRoot verificationEvidenceCanonicalPath
    if not (File.Exists path) then
        Result.Error [ EvidenceFileMissing path ]
    else
        try
            let lines = File.ReadAllLines path
            let results =
                lines
                |> Array.mapi (fun idx line ->
                    let lineNumber = idx + 1
                    if System.String.IsNullOrWhiteSpace line then
                        Result.Ok None
                    else
                        match parseVerificationEvidenceStrict line path lineNumber with
                        | Result.Ok v -> Result.Ok (Some v)
                        | Result.Error e -> Result.Error e)
                |> Array.toList

            // Separate successes and errors
            let errors = results |> List.choose (function Result.Error e -> Some e | Result.Ok _ -> None)
            if not (List.isEmpty errors) then
                Result.Error (errors |> List.map ParseError)
            else
                let records = results |> List.choose (function Result.Ok v -> v | _ -> None)

                // Check for duplicate and conflicting evidence records
                // Track both: (evidenceId * lineNumber * record) to distinguish duplicates from conflicts
                let evidenceWithLines =
                    records
                    |> List.mapi (fun idx r -> r.EvidenceId, (idx + 1), r)
                let idGroups = evidenceWithLines |> List.groupBy (fun (eid, _, _) -> eid)

                // Separate true duplicates (same ID, same content) from conflicts (same ID, different content)
                let mutable conflictErrors = []
                let mutable duplicateErrors = []
                for (id, entries) in idGroups do
                    match entries with
                    | (id, line1, r1) :: (id2, line2, r2) :: _ ->
                        // Multiple entries with same ID - check if content differs
                        if r1.EvidenceId <> r2.EvidenceId
                           || r1.EpisodeId <> r2.EpisodeId
                           || r1.Kind <> r2.Kind
                           || r1.Command <> r2.Command
                           || r1.ExitCode <> r2.ExitCode
                           || r1.Status <> r2.Status then
                            // Conflicting content for same ID
                            conflictErrors <- ConflictingEvidenceRecord(path, id, line1, line2) :: conflictErrors
                        else
                            // Same ID, same content - true duplicate
                            let lines = entries |> List.map (fun (_, l, _) -> l)
                            let minLine = lines |> List.min
                            let maxLine = lines |> List.max
                            duplicateErrors <- DuplicateEvidenceId(path, id, minLine, maxLine) :: duplicateErrors
                    | _ -> ()

                // Report conflicts first (they are more severe)
                if not (List.isEmpty conflictErrors) then
                    Result.Error conflictErrors
                elif not (List.isEmpty duplicateErrors) then
                    Result.Error duplicateErrors
                else
                    Result.Ok records
        with
        | :? IOException as ex ->
            Result.Error [ EvidenceFileUnreadable(path, ex.Message) ]
        | :? System.UnauthorizedAccessException as ex ->
            Result.Error [ EvidenceFileUnreadable(path, ex.Message) ]
        | ex ->
            Result.Error [ EvidenceFileUnreadable(path, ex.Message) ]

/// DEPRECATED: Do not use on the production qualification path.
/// This wraps loadVerificationEvidenceStrict but converts errors to empty list.
/// This defeats the fail-closed policy and must NOT be used for episode qualification.
/// Use loadVerificationEvidenceStrict directly and handle errors explicitly.
[<System.Obsolete("Use loadVerificationEvidenceStrict directly. This fails open and cannot be used for qualification.")>]
let loadVerificationEvidence (repoRoot: string) : VerificationEvidence list =
    match loadVerificationEvidenceStrict repoRoot with
    | Result.Ok records -> records
    | Result.Error _ -> []

/// Render a single declaration JSON file into a typed record.  Performs
/// schema-level validation and returns the list of issues found.
let parseDeclaration (json: string) (source: string option) : DeclarationValidation =
    try
        let v = parseJson json
        match v with
        | JsonObject fields ->
            let knownFields =
                [ "schema_version"; "episode_key"; "before_capture_id"; "after_capture_id"
                  "before_commit_oid"; "after_commit_oid"; "expected_before_tree_oid"
                  "expected_after_tree_oid"; "verification_evidence_ids"
                  "declared_relevant_paths"; "notes" ]
            let unknown =
                fields
                |> List.choose (fun (k, _) -> if List.contains k knownFields then None else Some k)
                |> List.map (fun k -> UnknownField k)
            let schemaVersion = lookupString fields "schema_version"
            let episodeKey = lookupString fields "episode_key"
            let beforeCap = lookupString fields "before_capture_id"
            let afterCap = lookupString fields "after_capture_id"
            let beforeOid = lookupString fields "before_commit_oid"
            let afterOid = lookupString fields "after_commit_oid"
            let expBefore = lookupOptString fields "expected_before_tree_oid"
            let expAfter = lookupOptString fields "expected_after_tree_oid"
            let verEvi = lookupStringList fields "verification_evidence_ids"
            let declared = lookupStringList fields "declared_relevant_paths"
            let notes = lookupOptString fields "notes"
            let mutable issues : DeclarationIssue list = unknown
            if schemaVersion <> Some RepairEpisodeDeclarationSchemaVersion then
                issues <- InvalidSchemaVersion :: issues
            if Option.isNone episodeKey then
                issues <- MissingField "episode_key" :: issues
            if Option.isNone beforeCap then
                issues <- MissingField "before_capture_id" :: issues
            if Option.isNone afterCap then
                issues <- MissingField "after_capture_id" :: issues
            if Option.isNone beforeOid then
                issues <- MissingField "before_commit_oid" :: issues
            if Option.isNone afterOid then
                issues <- MissingField "after_commit_oid" :: issues
            if List.isEmpty verEvi then
                issues <- MissingField "verification_evidence_ids" :: issues
            if List.isEmpty declared then
                issues <- MissingField "declared_relevant_paths" :: issues
            match episodeKey with
            | Some k when k.Length = 0 -> issues <- InvalidEpisodeKey :: issues
            | _ -> ()
            match beforeCap with
            | Some c when c.Length = 0 -> issues <- InvalidCaptureId :: issues
            | _ -> ()
            match afterCap with
            | Some c when c.Length = 0 -> issues <- InvalidCaptureId :: issues
            | _ -> ()
            match beforeOid with
            | Some o when o.Length <> 40 && o.Length <> 64 ->
                issues <- InvalidOidFormat(o, o.Length) :: issues
            | _ -> ()
            match afterOid with
            | Some o when o.Length <> 40 && o.Length <> 64 ->
                issues <- InvalidOidFormat(o, o.Length) :: issues
            | _ -> ()
            match expBefore with
            | Some o when o.Length <> 40 && o.Length <> 64 ->
                issues <- InvalidOidFormat(o, o.Length) :: issues
            | _ -> ()
            match expAfter with
            | Some o when o.Length <> 40 && o.Length <> 64 ->
                issues <- InvalidOidFormat(o, o.Length) :: issues
            | _ -> ()
            for p in declared do
                if System.IO.Path.IsPathRooted p then
                    issues <- AbsoluteDeclaredPath p :: issues
            match schemaVersion, episodeKey, beforeCap, afterCap, beforeOid, afterOid with
            | Some sv, Some ek, Some bc, Some ac, Some bo, Some ao when List.isEmpty issues ->
                let decl : RepairEpisodeDeclaration =
                    { SchemaVersion = sv
                      EpisodeKey = ek
                      BeforeCaptureId = bc
                      AfterCaptureId = ac
                      BeforeCommitOid = bo
                      AfterCommitOid = ao
                      ExpectedBeforeTreeOid = expBefore
                      ExpectedAfterTreeOid = expAfter
                      VerificationEvidenceIds = verEvi
                      DeclaredRelevantPaths = declared
                      Notes = notes }
                { Declaration = Some decl; Issues = []; Source = source }
            | _ ->
                { Declaration = None; Issues = issues |> List.rev; Source = source }
        | _ ->
            { Declaration = None; Issues = [ InvalidJson ]; Source = source }
    with
    | _ ->
        { Declaration = None; Issues = [ InvalidJson ]; Source = source }

let loadDeclarations (repoRoot: string) : (string * DeclarationValidation) list =
    enumerateDeclarationPaths repoRoot
    |> List.map (fun rel ->
        let fullPath = repoRelative repoRoot rel
        let text = readDeclaration fullPath
        rel, parseDeclaration text (Some rel))

let computeCompatibility (before: CaptureManifest) (after: CaptureManifest) : Compatibility =
    let mutable reasons : string list = []
    if before.CaptureKind <> after.CaptureKind then
        reasons <- (sprintf "capture_kind changed from %s to %s" before.CaptureKind after.CaptureKind) :: reasons
    match before.WorkingDirectory, after.WorkingDirectory with
    | Some b, Some a when canonicalise b <> canonicalise a ->
        reasons <- (sprintf "working_directory changed from %s to %s" (canonicalise b) (canonicalise a)) :: reasons
    | _, _ -> ()
    match before.DotnetSdkVersion, after.DotnetSdkVersion with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "dotnet_sdk_version changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    match before.MsbuildVersion, after.MsbuildVersion with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "msbuild_version changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    match before.FsharpCompilerVersion, after.FsharpCompilerVersion with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "fsharp_compiler_version changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    match before.OperatingSystem, after.OperatingSystem with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "operating_system changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    match before.Architecture, after.Architecture with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "architecture changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    match before.Culture, after.Culture with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "culture changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    let required =
        [ "command"; "working_directory"; "dotnet_sdk_version"; "msbuild_version"
          "fsharp_compiler_version"; "operating_system"; "architecture"; "culture" ]
    let isMissing (m: CaptureManifest) (field: string) : bool =
        match field with
        | "command" -> m.Command.IsNone
        | "working_directory" -> m.WorkingDirectory.IsNone
        | "dotnet_sdk_version" -> m.DotnetSdkVersion.IsNone
        | "msbuild_version" -> m.MsbuildVersion.IsNone
        | "fsharp_compiler_version" -> m.FsharpCompilerVersion.IsNone
        | "operating_system" -> m.OperatingSystem.IsNone
        | "architecture" -> m.Architecture.IsNone
        | "culture" -> m.Culture.IsNone
        | _ -> false
    let missing =
        required
        |> List.filter (fun f -> isMissing before f || isMissing after f)
    if not (List.isEmpty reasons) then
        { Status = Incompatible
          Reasons = reasons
          MissingFields = [] }
    elif not (List.isEmpty missing) then
        { Status = Unknown
          Reasons = []
          MissingFields = missing }
    else
        compatible

let private afterScopeOk (changes: GitChangeEntry list) (projectPath: string option) : bool =
    match projectPath with
    | None -> true
    | Some p -> not (hasChangeOfKind changes Deleted p)

let private qualification
    (compat: Compatibility)
    (changes: GitChangeEntry list)
    (afterScopeOk: bool)
    (verificationLevel: VerificationLevel)
    (transitions: DiagnosticTransition list)
    : EpisodeQualification =
    let mutable reasons : string list = []
    if compat.Status = Incompatible then
        reasons <- "incompatible before/after scope" :: reasons
    if not afterScopeOk then
        reasons <- "after-scope project path deleted" :: reasons
    if List.isEmpty changes && List.isEmpty transitions then
        reasons <- "no changes and no diagnostic transitions" :: reasons
    match verificationLevel with
    | TransitionObserved ->
        reasons <- "verification level is transition_observed" :: reasons
    | _ -> ()
    if List.isEmpty reasons then
        { Status = Qualified; Reasons = [] }
    elif verificationLevel = TransitionObserved || verificationLevel = SourceLinked then
        { Status = Ambiguous; Reasons = reasons }
    else
        { Status = QualifiedWithLimitations; Reasons = reasons }

let verificationLevelFromEvidence (items: VerificationEvidence list) : VerificationLevel =
    let mutable anyPass = false
    let mutable hasGate = false
    let mutable hasTest = false
    let mutable hasBuild = false
    for e in items do
        if e.Status = VerificationStatus.Pass then
            anyPass <- true
            match e.Kind with
            | FocusedGate -> hasGate <- true
            | FocusedTest -> hasTest <- true
            | Build -> hasBuild <- true
            | _ -> ()
    if hasGate then FocusedGateVerified
    elif hasTest then FocusedTestVerified
    elif hasBuild then BuildVerified
    elif anyPass then SourceLinked
    else TransitionObserved

/// Result type for successful episode engine execution.
type EpisodeEngineResult = {
    Summary: RepairEpisodeSummary
    RepairEpisodes: RepairEpisode list
    Transitions: DiagnosticTransition list
    ChangeSets: GitChangeSet list
    Verification: VerificationEvidence list
    Outcome: bool
    Declarations: (string * DeclarationValidation) list
}

/// Failure cases for the episode engine.
/// An EpisodeEngineResult MUST NOT be produced when evidence loading fails.
[<RequireQualifiedAccess>]
type EpisodeEngineFailure =
    /// Evidence loading failed with specific errors.
    | VerificationEvidenceLoadFailed of VerificationEvidenceLoadError list
    /// Declaration loading produced issues.
    | DeclarationLoadFailed of DeclarationIssue list
    /// Publication failed with atomic outcome details.
    | PublicationFailed of canonicalByteIdentical: bool * message: string
    /// Internal engine failure.
    | InternalFailure of operation: string * message: string

/// Execution outcome of the episode engine.
/// Separates success (EpisodeEngineResult) from failure (EpisodeEngineFailure).
/// Required invariant: EpisodeEngineResult exists ⇔ episode computation completed.
[<RequireQualifiedAccess>]
type EpisodeEngineExecution =
    /// Successful completion with result.
    | Completed of EpisodeEngineResult
    /// Engine failed with specific failure reason.
    | Failed of EpisodeEngineFailure


// =============================================================================
// Failure semantics documentation
// =============================================================================
//
// This section documents the current behavior of each EpisodeEngineFailure case
// and the semantic distinction from EpisodeEngineResult.
//
// DeclarationLoadFailed:
//   CURRENT BEHAVIOR: Invalid declarations produce a Completed result with the
//   issues recorded in the summary's InvalidDeclarations count. The engine does
//   NOT currently return EpisodeEngineFailure.DeclarationLoadFailed. Instead,
//   it skips invalid declarations and continues processing valid ones. This
//   means the result's Summary.InvalidDeclarations > 0 is the policy signal,
//   not a separate failure path.
//
// PublicationFailed:
//   CURRENT BEHAVIOR: Publication failures are represented in the Completed
//   result with Outcome = false. The engine does NOT currently return
//   EpisodeEngineFailure.PublicationFailed. The Outcome field is the signal,
//   not a separate failure path. The canonicalByteIdentical and message details
//   are captured in the EpisodeEngineFailure.PublicationFailed type for future
//   use when the publication path is made fail-closed.
//
// InternalFailure:
//   CURRENT BEHAVIOR: There is no production path that returns
//   EpisodeEngineFailure.InternalFailure. The type exists for future error
//   handling. All current internal errors are either caught and handled
//   gracefully or propagated as other failure types.
//
// VerificationEvidenceLoadFailed:
//   PRODUCTION BEHAVIOR: Evidence loading failures DO return Failed with
//   VerificationEvidenceLoadFailed. This is the primary fail-closed path
let private buildEpisodeId
    (beforeCap: string)
    (afterCap: string)
    (beforeTree: string)
    (afterTree: string)
    (changeSetId: string)
    : string =
    computeEpisodeId beforeCap afterCap beforeTree afterTree changeSetId

let private verificationIdFor
    (cmd: string)
    (episodeId: string)
    (kind: VerificationKind)
    : string =
    let sb = StringBuilder()
    let prefix (s: string) =
        sb.Append(s.Length.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)) |> ignore
        sb.Append(':') |> ignore
        sb.Append s |> ignore
    prefix VerificationEvidenceSchemaVersion
    prefix episodeId
    prefix (verificationKindToken kind)
    prefix cmd
    sha256OfUtf8 (sb.ToString())

/// Internal helper: run episode computation with pre-loaded evidence.
let private runEpisodesWithEvidence
    (repoRoot: string)
    (options: EpisodeEngineOptions)
    (declarations: (string * DeclarationValidation) list)
    (allEvidence: VerificationEvidence list)
    : EpisodeEngineResult =

    let keyCounts =
        declarations
        |> List.choose (fun (_, d) -> d.Declaration |> Option.map (fun d -> d.EpisodeKey))
        |> List.groupBy id
        |> List.map (fun (k, xs) -> k, List.length xs)
        |> Map.ofList
    let duplicateKeys =
        keyCounts
        |> Map.filter (fun _ c -> c > 1)
        |> Map.keys
        |> Seq.toList

    let validDeclarations =
        declarations
        |> List.choose (fun (_, d) -> d.Declaration)
        |> List.filter (fun d -> not (List.contains d.EpisodeKey duplicateKeys))

    let invalidCount =
        declarations
        |> List.filter (fun (_, d) -> not (List.isEmpty d.Issues))
        |> List.length

    let captures =
        validDeclarations
        |> List.map (fun d -> d.BeforeCaptureId, d.AfterCaptureId)
        |> List.collect (fun (b, a) -> [ b; a ])
        |> List.distinct
        |> List.map (fun id -> id, tryLoadCapture repoRoot id)
        |> Map.ofList

    let missingCaptures =
        captures
        |> Map.filter (fun _ v -> v.IsNone)
        |> Map.keys
        |> Seq.toList

    let mutable transitions : DiagnosticTransition list = []
    let mutable episodes : RepairEpisode list = []
    let mutable changeSets : GitChangeSet list = []
    let mutable evidence : VerificationEvidence list = []
    let mutable missingGitObjects = 0
    let mutable duplicateIds = 0
    let episodeIds = System.Collections.Generic.HashSet<string>()

    for decl in validDeclarations do
        if List.contains decl.BeforeCaptureId missingCaptures
           || List.contains decl.AfterCaptureId missingCaptures then
            ()
        else
            try
                let identity =
                    resolveGitIdentity repoRoot options.GitRunOptions decl.BeforeCommitOid decl.AfterCommitOid
                let beforeCap =
                    Map.find decl.BeforeCaptureId captures |> Option.get
                let afterCap =
                    Map.find decl.AfterCaptureId captures |> Option.get
                let changeSet =
                    buildChangeSet repoRoot options.GitRunOptions identity.ObjectFormat
                        identity.BeforeTreeOid identity.AfterTreeOid
                changeSets <- changeSet :: changeSets
                let compat = computeCompatibility beforeCap.Manifest afterCap.Manifest
                let projectPath = afterCap.Manifest.WorkingDirectory
                let afterOk = afterScopeOk changeSet.Entries projectPath
                let episodeId =
                    buildEpisodeId decl.BeforeCaptureId decl.AfterCaptureId
                        identity.BeforeTreeOid identity.AfterTreeOid changeSet.ChangeSetId
                if not (episodeIds.Add episodeId) then
                    duplicateIds <- duplicateIds + 1
                let transitionResult =
                    Transitions.buildTransitions
                        episodeId
                        compat
                        changeSet.Entries
                        decl.DeclaredRelevantPaths
                        beforeCap.Occurrences
                        afterCap.Occurrences
                transitions <- transitionResult.Transitions @ transitions
                // Filter evidence for this episode
                let episodeEvidence = allEvidence |> List.filter (fun e -> e.EpisodeId = episodeId)
                let verificationLevel = verificationLevelFromEvidence episodeEvidence
                let qual =
                    qualification compat changeSet.Entries afterOk verificationLevel transitions
                let contractBefore = commandContract beforeCap.Manifest
                let contractAfter = commandContract afterCap.Manifest
                let counts = transitionResult.Counts
                let episode : RepairEpisode =
                    { SchemaVersion = RepairEpisodeSchemaVersion
                      EpisodeId = episodeId
                      EpisodeKey = decl.EpisodeKey
                      BeforeCaptureId = decl.BeforeCaptureId
                      AfterCaptureId = decl.AfterCaptureId
                      BeforeCommitOid = identity.BeforeCommitOid
                      BeforeTreeOid = identity.BeforeTreeOid
                      AfterCommitOid = identity.AfterCommitOid
                      AfterTreeOid = identity.AfterTreeOid
                      CommitRange = identity.CommitRange
                      ChangeSetId = changeSet.ChangeSetId
                      CommandContractBefore = contractBefore
                      CommandContractAfter = contractAfter
                      Compatibility = compat
                      TransitionCounts = counts
                      VerificationLevel = verificationLevel
                      VerificationEvidenceIds = decl.VerificationEvidenceIds
                      Qualification = qual }
                episodes <- episode :: episodes
                evidence <- episodeEvidence @ evidence
                let beforeCommitOk =
                    match beforeCap.Manifest.RepositoryCommitOid with
                    | Some c -> c = identity.BeforeCommitOid
                    | None -> true
                let afterCommitOk =
                    match afterCap.Manifest.RepositoryCommitOid with
                    | Some c -> c = identity.AfterCommitOid
                    | None -> true
                if not beforeCommitOk || not afterCommitOk then
                    missingGitObjects <- missingGitObjects + 1
            with
            | GitIdentityFailure _ -> missingGitObjects <- missingGitObjects + 1
            | GitObjectFormatFailure _ -> missingGitObjects <- missingGitObjects + 1
            | GitChangeParseFailure _ -> missingGitObjects <- missingGitObjects + 1

    let sortedEpisodes = episodes |> List.sortBy (fun e -> e.EpisodeId)
    let sortedChangeSets = changeSets |> List.sortBy (fun cs -> cs.ChangeSetId)
    let sortedTransitions = transitions |> List.sortBy (fun t -> t.EpisodeId, t.ExactFingerprint)
    let sortedEvidence = evidence |> List.sortBy (fun e -> e.EvidenceId)

    let episodesBody =
        sortedEpisodes
        |> List.map renderRepairEpisode
        |> String.concat "\n"
    let transitionsBody =
        sortedTransitions
        |> List.map renderDiagnosticTransition
        |> String.concat "\n"
    let changeSetsBody =
        sortedChangeSets
        |> List.map renderGitChangeSet
        |> String.concat "\n"
    let evidenceBody =
        sortedEvidence
        |> List.map renderVerificationEvidence
        |> String.concat "\n"

    let summary =
        { SchemaVersion = RepairEpisodeSummarySchemaVersion
          DeclarationsTotal = List.length declarations
          ValidDeclarations = List.length validDeclarations
          InvalidDeclarations = invalidCount
          MissingCaptures = List.length missingCaptures
          MissingGitObjects = missingGitObjects
          DuplicateEpisodeKeys = List.length duplicateKeys
          DuplicateEpisodeIds = duplicateIds
          EpisodesTotal = List.length sortedEpisodes
          EpisodesQualified = sortedEpisodes |> List.filter (fun e -> e.Qualification.Status = Qualified) |> List.length
          EpisodesQualifiedWithLimitations = sortedEpisodes |> List.filter (fun e -> e.Qualification.Status = QualifiedWithLimitations) |> List.length
          EpisodesAmbiguous = sortedEpisodes |> List.filter (fun e -> e.Qualification.Status = Ambiguous) |> List.length
          EpisodesRejected = sortedEpisodes |> List.filter (fun e -> e.Qualification.Status = Rejected) |> List.length
          ChangeSetsTotal = List.length sortedChangeSets
          TransitionsTotal = List.length sortedTransitions
          PersistedSameCount = 0
          PersistedCountDecreased = 0
          PersistedCountIncreased = 0
          EliminatedAfter = 0
          IntroducedAfter = 0
          ResolutionCandidates = 0
          RegressionCandidates = 0
          UnassessableTransitions = 0
          VerificationEvidenceTotal = List.length sortedEvidence }
    let summaryBody = renderRepairEpisodeSummary summary

    let normalizedDir = repoRelative repoRoot normalizedSubdir
    if not (Directory.Exists normalizedDir) then
        Directory.CreateDirectory normalizedDir |> ignore

    let files =
        [
            { CanonicalFileName = repairEpisodesFile; Body = episodesBody }
            { CanonicalFileName = diagnosticTransitionsFile; Body = transitionsBody }
            { CanonicalFileName = gitChangeSetsFile; Body = changeSetsBody }
            { CanonicalFileName = repairEpisodeSummaryFile; Body = summaryBody }
            { CanonicalFileName = verificationEvidenceFile; Body = evidenceBody }
        ]
    let outcome = publish normalizedDir true false files
    { Summary = summary
      RepairEpisodes = sortedEpisodes
      Transitions = sortedTransitions
      ChangeSets = sortedChangeSets
      Verification = sortedEvidence
      Outcome = outcome.Success
      Declarations = declarations }

/// Run the episode engine with fail-closed error propagation.
/// If evidence loading fails, return EpisodeEngineExecution.Failed to preserve exact errors.
let runEpisodeEngine
    (repoRoot: string)
    (options: EpisodeEngineOptions)
    : EpisodeEngineExecution =
    clearObjectFormatCache ()

    let declarations = loadDeclarations repoRoot

    // Load verification evidence using strict loader - FAIL CLOSED on any error
    match loadVerificationEvidenceStrict repoRoot with
    | Result.Error loadErrors ->
        // Return failure with exact errors - do NOT produce EpisodeEngineResult
        EpisodeEngineExecution.Failed (EpisodeEngineFailure.VerificationEvidenceLoadFailed loadErrors)
    | Result.Ok allEvidence ->
        // Evidence loaded successfully, proceed with episode computation
        EpisodeEngineExecution.Completed (runEpisodesWithEvidence repoRoot options declarations allEvidence)

type VerificationIssue =
    | EpisodeIdMismatch
    | ChangeSetIdMismatch
    | TransitionCountMismatch
    | TransitionEpisodeIdMismatch
    | FileMissing of path: string
    | HashMismatch of path: string
    | ManifestMissing of path: string
    | SummaryMismatch
    | DeclarationInvalid of issues: int
    | VerificationEvidenceLoadFailed of errors: VerificationEvidenceLoadError list
    | EpisodeEngineFailed of failure: EpisodeEngineFailure

type VerificationResult = {
    Issues: VerificationIssue list
    RepairEpisodesValidated: int
    TransitionsValidated: int
}

let verifyPipeline
    (repoRoot: string)
    (options: EpisodeEngineOptions)
    : VerificationResult =
    match runEpisodeEngine repoRoot options with
    | EpisodeEngineExecution.Failed failure ->
        match failure with
        | EpisodeEngineFailure.VerificationEvidenceLoadFailed errors ->
            // Preserve exact verification evidence load errors
            { Issues = [ VerificationEvidenceLoadFailed errors ]
              RepairEpisodesValidated = 0
              TransitionsValidated = 0 }
        | EpisodeEngineFailure.DeclarationLoadFailed _ ->
            { Issues = [ EpisodeEngineFailed failure ]
              RepairEpisodesValidated = 0
              TransitionsValidated = 0 }
        | EpisodeEngineFailure.PublicationFailed _ ->
            { Issues = [ EpisodeEngineFailed failure ]
              RepairEpisodesValidated = 0
              TransitionsValidated = 0 }
        | EpisodeEngineFailure.InternalFailure _ ->
            { Issues = [ EpisodeEngineFailed failure ]
              RepairEpisodesValidated = 0
              TransitionsValidated = 0 }
    | EpisodeEngineExecution.Completed result ->
        // Successful completion - perform ordinary corpus verification
        let mutable issues : VerificationIssue list = []
        let expectedPaths =
            [ repairEpisodesCanonicalPath
              diagnosticTransitionsCanonicalPath
              gitChangeSetsCanonicalPath
              repairEpisodeSummaryCanonicalPath
              verificationEvidenceCanonicalPath ]
        for p in expectedPaths do
            let full = repoRelative repoRoot p
            if not (File.Exists full) then
                issues <- FileMissing p :: issues
        let invalidDecls =
            result.Declarations
            |> List.filter (fun (_, d) -> not (List.isEmpty d.Issues))
            |> List.length
        if invalidDecls > 0 then
            issues <- DeclarationInvalid invalidDecls :: issues
        { Issues = issues
          RepairEpisodesValidated = result.RepairEpisodes |> List.length
          TransitionsValidated = result.Transitions |> List.length }

let publicChangeSetId
    (beforeTree: string)
    (afterTree: string)
    (entries: GitChangeEntry list)
    : string =
    computeChangeSetIdentity beforeTree afterTree entries

let publicEpisodeId
    (beforeCap: string)
    (afterCap: string)
    (beforeTree: string)
    (afterTree: string)
    (changeSetId: string)
    : string =
    buildEpisodeId beforeCap afterCap beforeTree afterTree changeSetId

let publicEvidenceId
    (cmd: string)
    (episodeId: string)
    (kind: VerificationKind)
    : string =
    verificationIdFor cmd episodeId kind
