module Circus.Tooling.Tests.SourcePolicy.ContainerPolicyMutationRegistry

/// Authoritative immutable mutation registry for the container-policy
/// negative mutation suite.
///
/// P0-5 (CORRECTION01): one registry, one validation gate, one
/// execution pass, one immutable result map.  No global mutable
/// pass counter.  No global mutable case set.  No global mutable
/// result dictionary.  Every count and verdict is derived from the
/// result map at the assertion site.
///
/// The registry exposes the minimum needed to prepare, mutate,
/// execute, and classify a case; the case identifier is a private
/// comparable domain type so it cannot be replaced by a display
/// name, list index, or truncated rule prefix at the assertion site.

open System
open System.IO

open Circus.Tooling.SourcePolicy.ContainerPolicy

// ---------------------------------------------------------------------------
// Case identity
// ---------------------------------------------------------------------------

/// Private comparable case identifier.  The string is bound to the
/// exact concrete container-policy check id (e.g.
/// ``CP-10_trusted_runner``) so map keys cannot be confused with
/// display names, list indices, or truncated prefixes.  Construction
/// is restricted to this module via the ``private`` union case.
type MutationCaseId =
    private MutationCaseId of string

module MutationCaseId =
    let value (MutationCaseId v) = v

    /// Smart constructor: empty or whitespace-only ids are
    /// rejected.  A valid case identifier is therefore
    /// unrepresentable as the empty string.
    let tryCreate (s: string) : Result<MutationCaseId, string> =
        if String.IsNullOrWhiteSpace s then
            Result.Error "mutation case id must be non-empty"
        else
            Result.Ok (MutationCaseId s)

    let fromString (s: string) : MutationCaseId =
        match tryCreate s with
        | Result.Ok id -> id
        | Result.Error msg -> invalidArg "s" msg

    let compare (a: MutationCaseId) (b: MutationCaseId) : int =
        String.CompareOrdinal(value a, value b)

// ---------------------------------------------------------------------------
// Workspace seam (test-only injection point)
// ---------------------------------------------------------------------------

/// Workspace seam: every per-case execution flows through this pair
/// of functions.  Production code uses the defaults (real
/// filesystem IO).  Tests inject deterministic variants to prove
/// failure paths without relying on filesystem timing.
type WorkspaceSeam = {
    CreateTempDir: unit -> Result<string, string>
    DeleteRecursive: string -> Result<unit, string>
    RunCheck: string -> string -> Result<Violation list, string>
}

let defaultWorkspaceSeam : WorkspaceSeam = {
    CreateTempDir = fun () ->
        let path =
            Path.Combine(
                Path.GetTempPath(),
                "circus-cp-mut-" + Guid.NewGuid().ToString("n"))
        try
            Directory.CreateDirectory path |> ignore
            Ok path
        with ex ->
            Error (sprintf "could not create workspace: %s" ex.Message)
    DeleteRecursive = fun (root: string) ->
        try
            if Directory.Exists root then
                Directory.Delete(root, true)
            Ok ()
        with ex ->
            Error (sprintf "cleanup failed for %s: %s" root ex.Message)
    RunCheck = fun (id: string) (root: string) ->
        try
            Ok (runCheckById id root)
        with
        | CheckFailed msg ->
            Error (sprintf "check %s raised CheckFailed: %s" id msg)
        | ex ->
            Error (sprintf "check %s raised %s: %s" id (ex.GetType().Name) ex.Message)
}

// ---------------------------------------------------------------------------
// Mutation receipt
// ---------------------------------------------------------------------------

/// Concrete receipt proving a mutation actually changed something.
/// The mutator must return at least one entry per file that it
/// intends to change; the receipt is rejected if all before/after
/// hashes are identical, if any changed path escapes the workspace,
/// or if the receipt key sets are inconsistent.
type MutationReceipt = {
    ChangedPaths: string list
    BeforeHashes: Map<string, string>
    AfterHashes: Map<string, string>
} with
    member r.KeysMatch () : bool =
        let changed = r.ChangedPaths |> Set.ofList
        let before = r.BeforeHashes |> Map.toList |> List.map fst |> Set.ofList
        let after = r.AfterHashes |> Map.toList |> List.map fst |> Set.ofList
        changed = before && changed = after

    member r.HasObservableChange () : bool =
        r.ChangedPaths
        |> List.exists (fun p ->
            match Map.tryFind p r.BeforeHashes, Map.tryFind p r.AfterHashes with
            | Some b, Some a -> b <> a
            | _ -> false)

    /// Backward-compatible alias used by helpers and existing tests.
    member r.IsNonVacuous with get () = r.HasObservableChange ()


// ---------------------------------------------------------------------------
// Result model
// ---------------------------------------------------------------------------

type MutationSuccess = {
    CaseId: MutationCaseId
    ExpectedCheckId: string
    BaselineViolations: Violation list
    MutatedViolations: Violation list
    Receipt: MutationReceipt
}

type MutationFailure =
    | BaselinePreparationFailed of string
    | BaselineNotCompliant of Violation list
    | MutationApplicationFailed of string
    | MutationWasVacuous of string
    | ExpectedViolationMissing of expectedCheckId: string * actual: Violation list
    | UnexpectedViolation of Violation
    | CaseExecutionFailed of string
    | CleanupFailed of string

// ---------------------------------------------------------------------------
// Case model
// ---------------------------------------------------------------------------

type MutationCase = {
    Id: MutationCaseId
    Description: string
    ExpectedCheckId: string
    PrepareBaseline: string -> Result<unit, string>
    ApplyMutation: string -> Result<MutationReceipt, string>
    AllowedAdditionalCheckIds: Set<string>
}

// ---------------------------------------------------------------------------
// Registry validation (executed before any case body)
// ---------------------------------------------------------------------------

type RegistryValidation =
    | RegistryOk
    | DuplicateCaseIds of string list
    | MismatchedCaseIdentities of (string * string) list
    | UnknownExpectedCheckIds of string list

type RegistryExecutionFailure =
    | InvalidRegistry of RegistryValidation

type MutationResults =
    Map<MutationCaseId, Result<MutationSuccess, MutationFailure>>

type RegistryOutcome =
    Result<MutationResults, RegistryExecutionFailure>



let private allProductionCheckIds : Set<string> =
    set CheckIds

let validateMutationRegistry (cases: MutationCase list) : RegistryValidation =
    let duplicates =
        cases
        |> List.map (fun c -> MutationCaseId.value c.Id)
        |> List.groupBy id
        |> List.filter (fun (_, g) -> List.length g > 1)
        |> List.map fst
    if not (List.isEmpty duplicates) then
        DuplicateCaseIds duplicates
    else
        // P0-5 closure: every case must identify itself by the
        // exact check id it is targeting.  An ``Id`` of
        // ``CP-04_vacuous`` bound to ``ExpectedCheckId =
        // CP-04_workflow_triggers`` is no longer allowed.
        let mismatches =
            cases
            |> List.choose (fun c ->
                let cid = MutationCaseId.value c.Id
                if cid <> c.ExpectedCheckId then
                    Some (cid, c.ExpectedCheckId)
                else
                    None)
        if not (List.isEmpty mismatches) then
            MismatchedCaseIdentities mismatches
        else
            let unknown =
                cases
                |> List.map (fun c -> c.ExpectedCheckId)
                |> List.filter (fun id -> not (Set.contains id allProductionCheckIds))
                |> List.distinct
            if not (List.isEmpty unknown) then
                UnknownExpectedCheckIds unknown
            else
                RegistryOk

// ---------------------------------------------------------------------------
// Workspace containment
// ---------------------------------------------------------------------------

/// Resolves ``root/relative`` to an absolute path and rejects any
/// path that escapes the workspace via absolute, parent-relative,
/// or rooted-component smuggling.
let resolveContainedPath (root: string) (relativePath: string)
    : Result<string, string> =
    try
        let rootFull = Path.GetFullPath root
        let candidate =
            if Path.IsPathRooted relativePath then
                Path.GetFullPath relativePath
            else
                Path.GetFullPath(Path.Combine(rootFull, relativePath))
        let rel = Path.GetRelativePath(rootFull, candidate)
        let escapes =
            String.IsNullOrEmpty rel
            || rel = ".."
            || rel.StartsWith(".." + string Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted rel
        if escapes then
            Error (sprintf "path escapes workspace: %s" relativePath)
        else
            Ok candidate
    with ex ->
        Error (sprintf "resolveContainedPath failed for %s: %s" relativePath ex.Message)

/// Build a ``MutationReceipt`` from one or more ``writeAndHash``
/// results.  The mutator composes these helpers to construct a
/// non-vacuous receipt without touching any global state.
let buildReceipt
    (items: (string * Result<string * string, string>) list)
    : Result<MutationReceipt, string> =
    let mutable firstError : string option = None
    let mutable changed : string list = []
    let mutable before : Map<string, string> = Map.empty
    let mutable after : Map<string, string> = Map.empty
    for rel, r in items do
        match r, firstError with
        | Error e, None -> firstError <- Some e
        | Ok (b, a), None ->
            changed <- rel :: changed
            before <- Map.add rel b before
            after <- Map.add rel a after
        | _ -> ()
    match firstError with
    | Some e -> Error e
    | None ->
        Ok {
            ChangedPaths = List.rev changed
            BeforeHashes = before
            AfterHashes = after
        }

/// Make a script executable in the workspace.
let makeExecutable (root: string) (rel: string) : Result<unit, string> =
    try
        let full =
            Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
        let info = new FileInfo(full)
        info.UnixFileMode <-
            UnixFileMode.UserRead
            ||| UnixFileMode.UserWrite
            ||| UnixFileMode.UserExecute
        info.UnixFileMode <-
            info.UnixFileMode
            ||| UnixFileMode.GroupExecute
            ||| UnixFileMode.OtherExecute
        Ok ()
    with ex ->
        Error (sprintf "makeExecutable failed for %s: %s" rel ex.Message)

// ---------------------------------------------------------------------------
// Per-case executor
// ---------------------------------------------------------------------------

let private sha256OfFile (path: string) : string =
    use fs = File.OpenRead(path)
    use sha = System.Security.Cryptography.SHA256.Create()
    let bytes = sha.ComputeHash(fs)
    BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()

/// Write ``content`` to ``root/rel`` and return a (before, after)
/// hash pair.  When the file is absent before the call, the before
/// slot is the empty string and the receipt is still non-vacuous
/// (creation counts as a change).  ``rel`` is rejected if it would
/// escape the workspace.
let writeAndHash (root: string) (rel: string) (newContent: string)
    : Result<string * string, string> =
    match resolveContainedPath root rel with
    | Error e -> Error e
    | Ok full ->
        try
            let dir = Path.GetDirectoryName full
            if not (Directory.Exists dir) then
                Directory.CreateDirectory dir |> ignore
            let beforeHash =
                if File.Exists full then sha256OfFile full
                else ""
            File.WriteAllText(full, newContent)
            let a = sha256OfFile full
            Ok (beforeHash, a)
        with ex ->
            Error (sprintf "writeAndHash failed for %s: %s" rel ex.Message)

let private collectWorkspaceFiles (root: string) : string list =
    let rec walk (dir: string) : string list =
        let mutable acc : string list = []
        if Directory.Exists dir then
            for f in Directory.GetFiles(dir) do
                let rel =
                    f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, '/')
                acc <- rel :: acc
            for d in Directory.GetDirectories(dir) do
                acc <- walk d @ acc
        acc
    walk root

/// Snapshot of the workspace: every relative path mapped to its
/// SHA-256 hash at the moment of the snapshot.  Used to verify
/// that the mutator actually changed the filesystem and did not
/// just hand the executor a fabricated receipt.
let snapshotWorkspace (root: string) : Map<string, string> =
    collectWorkspaceFiles root
    |> List.map (fun rel ->
        let full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
        rel, sha256OfFile full)
    |> Map.ofList

/// Derive the set of paths that genuinely changed between two
/// snapshots: every path present in either snapshot whose hash
/// differs (or is missing on one side).
let deriveChangedPaths
    (before: Map<string, string>)
    (after: Map<string, string>)
    : Set<string> =
    let allKeys =
        Set.union
            (before |> Map.keys |> Set.ofSeq)
            (after |> Map.keys |> Set.ofSeq)
    Set.filter (fun path ->
        Map.tryFind path before <> Map.tryFind path after) allKeys

let private combineFailures
    (primary: MutationFailure)
    (cleanupMsg: string)
    : MutationFailure =
    let primaryMsg =
        match primary with
        | BaselinePreparationFailed m -> sprintf "baseline preparation failed: %s" m
        | BaselineNotCompliant vs -> sprintf "baseline not compliant (%d violations)" (List.length vs)
        | MutationApplicationFailed m -> sprintf "mutation application failed: %s" m
        | MutationWasVacuous m -> sprintf "mutation was vacuous: %s" m
        | ExpectedViolationMissing (expected, actual) ->
            sprintf "expected violation %s missing (%d observed)" expected (List.length actual)
        | UnexpectedViolation v -> sprintf "unexpected violation: %s %s" v.Id v.Path
        | CaseExecutionFailed m -> m
        | CleanupFailed m -> m
    CaseExecutionFailed (sprintf "%s; cleanup also failed: %s" primaryMsg cleanupMsg)

let private executeCase
    (c: MutationCase)
    (seam: WorkspaceSeam)
    : Result<MutationSuccess, MutationFailure> =
    match seam.CreateTempDir () with
    | Error e ->
        Error (BaselinePreparationFailed e)
    | Ok root ->
        let runOnce () : Result<MutationSuccess, MutationFailure> =
            // Phase A: materialise the baseline.
            let baselineStep () : Result<unit, MutationFailure> =
                try
                    match c.PrepareBaseline root with
                    | Ok () -> Ok ()
                    | Error msg -> Error (BaselinePreparationFailed msg)
                with ex ->
                    Error (CaseExecutionFailed
                        (sprintf "PrepareBaseline for %s threw %s: %s"
                            (MutationCaseId.value c.Id) (ex.GetType().Name) ex.Message))
            match baselineStep () with
            | Error e -> Error e
            | Ok () ->
                // Phase B: baseline proof.
                match seam.RunCheck c.ExpectedCheckId root with
                | Error e -> Error (CaseExecutionFailed e)
                | Ok baselineViolations ->
                    if not (List.isEmpty baselineViolations) then
                        Error (BaselineNotCompliant baselineViolations)
                    else
                        // Phase C: mutate.
                        let mutationStep () : Result<MutationReceipt, MutationFailure> =
                            try
                                match c.ApplyMutation root with
                                | Ok r -> Ok r
                                | Error msg -> Error (MutationApplicationFailed msg)
                            with ex ->
                                Error (CaseExecutionFailed
                                    (sprintf "ApplyMutation for %s threw %s: %s"
                                        (MutationCaseId.value c.Id) (ex.GetType().Name) ex.Message))
                        match mutationStep () with
                        | Error e -> Error e
                        | Ok receipt ->
                            // Validate the receipt independently of the mutator
                            // by comparing its claimed before/after hashes to
                            // an executor-supplied snapshot of the real
                            // filesystem.
                            let beforeSnap = snapshotWorkspace root
                            match c.ApplyMutation root with
                            | Error e ->
                                Error (MutationApplicationFailed e)
                            | Ok receipt ->
                                let afterSnap = snapshotWorkspace root
                                let actualChanged = deriveChangedPaths beforeSnap afterSnap
                                let claimedChanged =
                                    receipt.ChangedPaths |> Set.ofList
                                if actualChanged = Set.empty then
                                    Error (MutationWasVacuous
                                        (sprintf "mutator for %s changed no paths on disk"
                                            (MutationCaseId.value c.Id)))
                                elif not (receipt.HasObservableChange ()) then
                                    Error (MutationWasVacuous
                                        (sprintf "mutator for %s produced no observable change"
                                            (MutationCaseId.value c.Id)))
                                elif actualChanged <> claimedChanged then
                                    Error (MutationApplicationFailed
                                        (sprintf "mutator for %s claimed changed paths %A but actual changed paths are %A"
                                            (MutationCaseId.value c.Id)
                                            (Set.toList claimedChanged)
                                            (Set.toList actualChanged)))
                                else
                                    // Receipt hashes must match the executor
                                    // snapshot restricted to the claimed paths.
                                    let actualReceipt =
                                        { receipt with
                                            BeforeHashes = Map.filter (fun k _ -> Set.contains k actualChanged) beforeSnap
                                            AfterHashes = Map.filter (fun k _ -> Set.contains k actualChanged) afterSnap }
                                    if not (actualReceipt.KeysMatch ()) then
                                        Error (MutationApplicationFailed
                                            (sprintf "mutator for %s receipt key sets do not match observed changed paths"
                                                (MutationCaseId.value c.Id)))
                                    else
                                        // Inject the verified receipt into the
                                        // next phase.
                                        Some actualReceipt
                                // Phase D: detection proof.
                                match seam.RunCheck c.ExpectedCheckId root with
                                    | Error e -> Error (CaseExecutionFailed e)
                                    | Ok mutatedViolations ->
                                        let targetIds = set [ c.ExpectedCheckId ]
                                        let allowedIds = targetIds + c.AllowedAdditionalCheckIds
                                        let observedIds =
                                            mutatedViolations
                                            |> List.map (fun v -> v.Id)
                                            |> List.distinct
                                            |> Set.ofList
                                        if not (Set.contains c.ExpectedCheckId observedIds) then
                                            Error (ExpectedViolationMissing (c.ExpectedCheckId, mutatedViolations))
                                        else
                                            let unexpected =
                                                mutatedViolations
                                                |> List.filter (fun v -> not (Set.contains v.Id allowedIds))
                                            match unexpected with
                                            | first :: _ -> Error (UnexpectedViolation first)
                                            | [] ->
                                                match actualReceipt with
                                                | Some verified ->
                                                    Ok {
                                                        CaseId = c.Id
                                                        ExpectedCheckId = c.ExpectedCheckId
                                                        BaselineViolations = []
                                                        MutatedViolations = mutatedViolations
                                                        Receipt = verified
                                                    }
                                                | None ->
                                                    Error (CaseExecutionFailed
                                                        (sprintf "case %s finished without a verified receipt"
                                                            (MutationCaseId.value c.Id)))
        match runOnce () with
        | Ok _ as ok ->
            match seam.DeleteRecursive root with
            | Ok () -> ok
            | Error e -> Error (CleanupFailed e)
        | Error primary as err ->
            match seam.DeleteRecursive root with
            | Ok () -> err
            | Error e -> Error (combineFailures primary e)

// ---------------------------------------------------------------------------
// Registry execution
// ---------------------------------------------------------------------------

/// Execute every registered case once.  Returns the immutable
/// ``Map<MutationCaseId, Result<MutationSuccess, MutationFailure>>``
/// on success, or ``Error (InvalidRegistry ...)`` if the registry
/// itself is invalid.  When the registry is invalid no case body
/// runs and no result map is constructed.
let executeMutationRegistry
    (cases: MutationCase list)
    : RegistryOutcome =
    match validateMutationRegistry cases with
    | RegistryOk ->
        cases
        |> List.map (fun c -> c.Id, executeCase c defaultWorkspaceSeam)
        |> Map.ofList
        |> Ok
    | invalid ->
        Error (InvalidRegistry invalid)

/// Variant of ``executeMutationRegistry`` that accepts an explicit
/// workspace seam.  Tests use this to prove failure paths
/// deterministically.
let executeMutationRegistryWithSeam
    (cases: MutationCase list)
    (seam: WorkspaceSeam)
    : RegistryOutcome =
    match validateMutationRegistry cases with
    | RegistryOk ->
        cases
        |> List.map (fun c -> c.Id, executeCase c seam)
        |> Map.ofList
        |> Ok
    | invalid ->
        Error (InvalidRegistry invalid)

// ---------------------------------------------------------------------------
// Pure derived views (no shared state, computed from inputs only)
// ---------------------------------------------------------------------------

let registeredIds (cases: MutationCase list) : Set<MutationCaseId> =
    cases |> List.map (fun c -> c.Id) |> Set.ofList

let resultIds (results: Map<MutationCaseId, Result<MutationSuccess, MutationFailure>>) : Set<MutationCaseId> =
    results |> Map.keys |> Set.ofSeq

let registeredCount (cases: MutationCase list) : int = List.length cases

let executedCount (results: Map<MutationCaseId, Result<MutationSuccess, MutationFailure>>) : int =
    Map.count results

let passedCount (results: Map<MutationCaseId, Result<MutationSuccess, MutationFailure>>) : int =
    results
    |> Map.values
    |> Seq.filter (function Result.Ok _ -> true | _ -> false)
    |> Seq.length

let failedCount (results: Map<MutationCaseId, Result<MutationSuccess, MutationFailure>>) : int =
    results
    |> Map.values
    |> Seq.filter (function Result.Error _ -> true | _ -> false)
    |> Seq.length

let missingResultIds
    (cases: MutationCase list)
    (results: Map<MutationCaseId, Result<MutationSuccess, MutationFailure>>)
    : Set<MutationCaseId> =
    Set.difference (registeredIds cases) (resultIds results)

let unexpectedResultIds
    (cases: MutationCase list)
    (results: Map<MutationCaseId, Result<MutationSuccess, MutationFailure>>)
    : Set<MutationCaseId> =
    Set.difference (resultIds results) (registeredIds cases)

let duplicateRegisteredIds (cases: MutationCase list) : string list =
    cases
    |> List.map (fun c -> MutationCaseId.value c.Id)
    |> List.groupBy id
    |> List.filter (fun (_, g) -> List.length g > 1)
    |> List.map fst

/// Render an ordered, deterministic failure summary keyed by
/// ``MutationCaseId``.  Used by the aggregate test to expose one
/// section per failed case.
let renderFailureSummary
    (results: Map<MutationCaseId, Result<MutationSuccess, MutationFailure>>)
    : string =
    results
    |> Map.toList
    |> List.sortBy (fun (id, _) -> MutationCaseId.value id)
    |> List.choose (fun (id, r) ->
        match r with
        | Result.Ok _ -> None
        | Result.Error failure ->
            let idStr = MutationCaseId.value id
            let detail =
                match failure with
                | BaselinePreparationFailed m -> sprintf "baseline preparation failed: %s" m
                | BaselineNotCompliant vs -> sprintf "baseline not compliant: %A" vs
                | MutationApplicationFailed m -> sprintf "mutation application failed: %s" m
                | MutationWasVacuous m -> sprintf "mutation was vacuous: %s" m
                | ExpectedViolationMissing (expected, actual) ->
                    sprintf "expected violation %s missing; observed: %A" expected actual
                | UnexpectedViolation v ->
                    sprintf "unexpected violation: %s %s %s" v.Id v.Path v.Detail
                | CaseExecutionFailed m -> sprintf "case execution failed: %s" m
                | CleanupFailed m -> sprintf "cleanup failed: %s" m
            Some (sprintf "- %s: %s" idStr detail))
    |> String.concat "\n"
