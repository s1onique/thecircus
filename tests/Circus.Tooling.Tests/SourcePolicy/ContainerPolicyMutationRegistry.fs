module Circus.Tooling.Tests.SourcePolicy.ContainerPolicyMutationRegistry

/// Authoritative immutable mutation registry for the container-policy
/// negative mutation suite.
///
/// P0-5 (CORRECTION01): one registry, one execution, one immutable result
/// map.  No global mutable pass counter, no global mutable case set, no
/// global mutable result dictionary.  Every count and verdict is
/// derived from the result map at the assertion site.
///
/// The registry exposes the minimum needed to prepare, mutate, execute,
/// and classify a case; the case identifier is a private comparable
/// domain type so it cannot be replaced by a display name, list index,
/// or truncated rule prefix at the assertion site.

open System
open System.IO
open System.Security.Cryptography

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

    /// Internal factory used exclusively by the registry module when
    /// binding an authoritative case definition.  Construction
    /// outside the module is impossible because the union case is
    /// ``private``.
    let fromString (s: string) : MutationCaseId =
        if String.IsNullOrEmpty s then
            invalidArg "s" "mutation case id must be non-empty"
        MutationCaseId s

    let compare (a: MutationCaseId) (b: MutationCaseId) : int =
        String.CompareOrdinal(value a, value b)

// ---------------------------------------------------------------------------
// Mutation receipt
// ---------------------------------------------------------------------------

/// Concrete receipt proving a mutation actually changed something.
/// The mutator must return at least one entry per file that it
/// intends to change; the receipt is rejected if all before/after
/// hashes are identical or if the changed path set is empty.
type MutationReceipt = {
    ChangedPaths: string list
    BeforeHashes: Map<string, string>
    AfterHashes: Map<string, string>
} with
    member r.IsNonVacuous =
        not (List.isEmpty r.ChangedPaths)
        && (r.ChangedPaths
            |> List.exists (fun p ->
                match Map.tryFind p r.BeforeHashes, Map.tryFind p r.AfterHashes with
                | Some b, Some a -> b <> a
                | _ -> false))

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
// Registry validation
// ---------------------------------------------------------------------------

type RegistryValidation =
    | RegistryOk
    | DuplicateCaseIds of string list
    | EmptyCaseIds of string list
    | UnknownExpectedCheckIds of string list

let private allProductionCheckIds : Set<string> =
    set CheckIds

let validateRegistry (cases: MutationCase list) : RegistryValidation =
    let emptyIds =
        cases
        |> List.filter (fun c -> String.IsNullOrEmpty (MutationCaseId.value c.Id))
        |> List.map (fun c -> MutationCaseId.value c.Id)
    if not (List.isEmpty emptyIds) then
        EmptyCaseIds emptyIds
    else
        let duplicates =
            cases
            |> List.map (fun c -> MutationCaseId.value c.Id)
            |> List.groupBy id
            |> List.filter (fun (_, g) -> List.length g > 1)
            |> List.map fst
        if not (List.isEmpty duplicates) then
            DuplicateCaseIds duplicates
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
// Hashing helpers (pure, isolated to mutation workspace)
// ---------------------------------------------------------------------------

let sha256OfFile (path: string) : string =
    use fs = File.OpenRead(path)
    use sha = SHA256.Create()
    let bytes = sha.ComputeHash(fs)
    BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant()

/// Write ``content`` to ``root/rel`` and produce a (before, after)
/// hash pair for the receipt.  When the file is absent before the
/// call, the ``before`` slot is the empty string and the receipt is
/// still non-vacuous (creation counts as a change).  When the file
/// already exists, both slots are real SHA-256 hashes.
let writeAndHash (root: string) (rel: string) (newContent: string)
    : Result<string * string, string> =
    try
        let full =
            Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
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
let makeExecutable (root: string) (rel: string) : unit =
    let full =
        Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
    try
        let info = new FileInfo(full)
        info.UnixFileMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        info.UnixFileMode <- info.UnixFileMode ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherExecute
    with _ -> ()

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

// ---------------------------------------------------------------------------
// Per-case executor
// ---------------------------------------------------------------------------

let private safeDelete (root: string) : Result<unit, string> =
    try
        if Directory.Exists root then
            Directory.Delete(root, true)
        Ok ()
    with ex ->
        Error (sprintf "cleanup failed for %s: %s" root ex.Message)

/// Combine a primary failure with a cleanup failure.  We preserve
/// the primary failure identity and append the cleanup diagnostic so
/// the original error is not overwritten.
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

let private runWithCleanup
    (root: string)
    (body: unit -> Result<'a, MutationFailure>)
    : Result<'a, MutationFailure> =
    match body () with
    | Ok _ as ok ->
        match safeDelete root with
        | Ok () -> ok
        | Error e -> Error (CleanupFailed e)
    | Error primary as err ->
        match safeDelete root with
        | Ok () -> err
        | Error e -> Error (combineFailures primary e)

let private executeCase
    (c: MutationCase)
    : Result<MutationSuccess, MutationFailure> =
    let root =
        Path.Combine(
            Path.GetTempPath(),
            "circus-cp-mut-" + Guid.NewGuid().ToString("n"))
    let workspaceOk =
        try
            Directory.CreateDirectory root |> ignore
            Ok ()
        with ex ->
            Error (BaselinePreparationFailed (sprintf "could not create workspace: %s" ex.Message))
    match workspaceOk with
    | Error e ->
        // No workspace exists; nothing to clean up.
        Error e
    | Ok () ->
        runWithCleanup root (fun () ->
            // Phase A: materialise the baseline.
            match c.PrepareBaseline root with
            | Error e ->
                Error (BaselinePreparationFailed e)
            | Ok () ->
                // Phase B: baseline proof.  We must catch the
                // ``CheckFailed`` exception that the production
                // ``readText`` raises when a required file is
                // missing; the absence of a required file is
                // itself a baseline-preparation failure, not a
                // detected violation.
                let safeCheck (id: string) : Result<Violation list, MutationFailure> =
                    try
                        Ok (runCheckById id root)
                    with
                    | CheckFailed msg ->
                        Error (CaseExecutionFailed (sprintf "check %s raised CheckFailed: %s" id msg))
                    | ex ->
                        Error (CaseExecutionFailed (sprintf "check %s raised %s: %s" id (ex.GetType().Name) ex.Message))
                match safeCheck c.ExpectedCheckId with
                | Error e -> Error e
                | Ok baselineViolations ->
                    if not (List.isEmpty baselineViolations) then
                        Error (BaselineNotCompliant baselineViolations)
                    else
                        match c.ApplyMutation root with
                        | Error e ->
                            Error (MutationApplicationFailed e)
                        | Ok receipt ->
                            if not receipt.IsNonVacuous then
                                Error (MutationWasVacuous (sprintf "mutator for %s produced no observable change" (MutationCaseId.value c.Id)))
                            else
                                // Phase D: detection proof.
                                match safeCheck c.ExpectedCheckId with
                                | Error e -> Error e
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
                                            Ok {
                                                CaseId = c.Id
                                                ExpectedCheckId = c.ExpectedCheckId
                                                BaselineViolations = []
                                                MutatedViolations = mutatedViolations
                                                Receipt = receipt
                                            })

// ---------------------------------------------------------------------------
// Registry execution
// ---------------------------------------------------------------------------

/// Execute every registered case once.  Returns an immutable
/// ``Map<MutationCaseId, Result<...>>`` keyed by exact case id.  No
/// global mutable state, no shared counters, no shared sets.
let executeMutationRegistry
    (cases: MutationCase list)
    : Map<MutationCaseId, Result<MutationSuccess, MutationFailure>> =
    cases
    |> List.map (fun c -> c.Id, executeCase c)
    |> Map.ofList

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
