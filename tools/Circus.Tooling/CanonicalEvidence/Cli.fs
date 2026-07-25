module Circus.Tooling.CanonicalEvidence.Cli

// =============================================================================
// Canonical evidence – CLI dispatcher
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION02
//
// Slice 7: CLI verb and registration.
//
// Invocations:
//
//   circus-tooling canonical-evidence regenerate \
//     --repo-root <path> \
//     --output <path> \
//     --baseline-commit <oid> \
//     [--scope-declaration <path>]
//
//   circus-tooling canonical-evidence verify \
//     --repo-root <path> \
//     --input <path> \
//     [--scope-declaration <path>]
//
// CORRECTION02 introduces a --scope-declaration CLI argument that
// supplies the active ACT's scope declaration path.  When omitted,
// the CLI falls back to the tracked repository pointer
// ``.factory/active-scope.json`` which is required to exist.  The
// pointer's ``declaration_path`` field is consumed verbatim.
//
// The CLI exposes the two verbs the task requires. Exit codes:
//
//   0 - pass
//   1 - policy failure (provider ran, evidence disagreed)
//   2 - operational failure (provider could not run)
//
// On any failure path the CLI prints a diagnostic line to stderr
// and NEVER prints a PASS line.
// =============================================================================

open System
open System.IO

open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.Provider
open Circus.Tooling.CanonicalEvidence.Serialization
open Circus.Tooling.CanonicalEvidence.Validation

module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

type Command =
    | RegenerateCmd of repoRoot: string * outputPath: string * baselineCommit: string * scopeDeclaration: string option
    | VerifyCmd of repoRoot: string * inputPath: string * scopeDeclaration: string option
    | HelpCmd

let helpText () : string =
    "canonical-evidence — repository-owned canonical evidence provider\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling canonical-evidence regenerate \\\n"
    + "    --repo-root <path> --output <path> --baseline-commit <oid> [--scope-declaration <path>]\n"
    + "  circus-tooling canonical-evidence verify \\\n"
    + "    --repo-root <path> --input <path> [--scope-declaration <path>]\n"
    + "  circus-tooling canonical-evidence help\n"

let private consumeFlag (flag: string) (args: string list) : Result<string * string list, string> =
    match args with
    | v :: rest -> Ok (v, rest)
    | _ -> Error (sprintf "missing value for %s" flag)

/// Resolve the active scope declaration path.  Preference order:
///
///   1. The ``--scope-declaration <path>`` CLI argument.
///
///   2. The tracked repository pointer ``<repoRoot>/.factory/active-scope.json``.
///
/// If neither source is present, the protected-scope check is
/// unavailable and the canonical evidence regeneration must fail
/// closed; the empty string is returned and the caller must treat
/// that as a missing declaration.
let private resolveScopeDeclaration (repoRoot: string) (scopeDeclaration: string option) : string =
    match scopeDeclaration with
    | Some s when not (String.IsNullOrWhiteSpace s) -> s
    | _ ->
        let pointerPath = Path.Combine(repoRoot, ".factory", "active-scope.json")
        if File.Exists pointerPath then
            // Simple line-based parser: the active-scope.json is a
            // strict JSON object with a single "declaration_path"
            // string field.  The full F# validator lives in the
            // tooling tests; this minimal parser is sufficient for
            // the CLI's fallback path.
            let lines = File.ReadAllLines(pointerPath)
            let mutable declPath = ""
            for line in lines do
                let trimmed = line.Trim().TrimEnd(',')
                if trimmed.StartsWith("\"declaration_path\"") then
                    let colonIdx = trimmed.IndexOf(':')
                    if colonIdx > 0 then
                        let afterColon = trimmed.Substring(colonIdx + 1).Trim()
                        if afterColon.StartsWith("\"") then
                            let startIdx = 1
                            let endIdx = afterColon.LastIndexOf("\"")
                            if endIdx > startIdx then
                                declPath <- afterColon.Substring(startIdx, endIdx - startIdx)
            declPath
        else
            ""

let private parse (argv: string list) : Result<Command, string> =
    match argv with
    | [] | [ "help" ] | [ "-h" ] | [ "--help" ] -> Ok HelpCmd
    | [ "regenerate" ] ->
        Error "regenerate requires --repo-root, --output, and --baseline-commit"
    | [ "verify" ] ->
        Error "verify requires --repo-root and --input"
    | "regenerate" :: rest ->
        let mutable repoRoot : string option = None
        let mutable outputPath : string option = None
        let mutable baselineCommit : string option = None
        let mutable scopeDecl : string option = None
        let mutable remaining = rest
        let mutable bad = false
        while not bad && not (List.isEmpty remaining) do
            match remaining with
            | "--repo-root" :: t ->
                match consumeFlag "--repo-root" t with
                | Ok (v, r) -> repoRoot <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | "--output" :: t ->
                match consumeFlag "--output" t with
                | Ok (v, r) -> outputPath <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | "--baseline-commit" :: t ->
                match consumeFlag "--baseline-commit" t with
                | Ok (v, r) -> baselineCommit <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | "--scope-declaration" :: t ->
                match consumeFlag "--scope-declaration" t with
                | Ok (v, r) -> scopeDecl <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | unknown :: _ ->
                bad <- true
                stderr.WriteLine(sprintf "error: unrecognised argument: %s" unknown)
            | [] ->
                bad <- true
        if bad then Error "argument parse failed"
        else
            match repoRoot, outputPath, baselineCommit with
            | Some r, Some o, Some b -> Ok(RegenerateCmd (r, o, b, scopeDecl))
            | _ -> Error "regenerate requires --repo-root, --output, and --baseline-commit"
    | "verify" :: rest ->
        let mutable repoRoot : string option = None
        let mutable inputPath : string option = None
        let mutable scopeDecl : string option = None
        let mutable remaining = rest
        let mutable bad = false
        while not bad && not (List.isEmpty remaining) do
            match remaining with
            | "--repo-root" :: t ->
                match consumeFlag "--repo-root" t with
                | Ok (v, r) -> repoRoot <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | "--input" :: t ->
                match consumeFlag "--input" t with
                | Ok (v, r) -> inputPath <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | "--scope-declaration" :: t ->
                match consumeFlag "--scope-declaration" t with
                | Ok (v, r) -> scopeDecl <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | unknown :: _ ->
                bad <- true
                stderr.WriteLine(sprintf "error: unrecognised argument: %s" unknown)
            | [] ->
                bad <- true
        if bad then Error "argument parse failed"
        else
            match repoRoot, inputPath with
            | Some r, Some i -> Ok(VerifyCmd (r, i, scopeDecl))
            | _ -> Error "verify requires --repo-root and --input"
    | _ ->
        Error "usage: canonical-evidence {regenerate|verify|help}"

// -----------------------------------------------------------------------------
// Render helpers
// -----------------------------------------------------------------------------

let private renderRegenerateSummary (e: CanonicalEvidence) (outputPath: string) (written: string) : string =
    sprintf
        "canonical-evidence regenerate: written=%s bytes_sha256=%s schema_version=%d provider=%s/%s overall=%s commit=%s tree=%s checks=%d"
        outputPath
        written
        e.SchemaVersion
        e.ProviderName
        e.ProviderVersion
        (statusToken e.OverallStatus)
        (if e.TestedCommitOid.Length >= 12 then e.TestedCommitOid.Substring(0, 12) else e.TestedCommitOid)
        (if e.TestedTreeOid.Length >= 12 then e.TestedTreeOid.Substring(0, 12) else e.TestedTreeOid)
        (List.length e.Checks)

let private renderLegacyVerifySummary (r: VerifyOutcome) : string =
    let status =
        match r.Failure with
        | None -> "PASS"
        | Some _ -> "FAIL"
    let reasons =
        match r.Failure with
        | Some f -> verifyFailureToString f
        | None -> ""
    let commitPrefix =
        match r.Evidence with
        | Some e when e.TestedCommitOid.Length >= 12 -> e.TestedCommitOid.Substring(0, 12)
        | _ -> "?"
    let treePrefix =
        match r.Evidence with
        | Some e when e.TestedTreeOid.Length >= 12 -> e.TestedTreeOid.Substring(0, 12)
        | _ -> "?"
    if String.IsNullOrEmpty reasons then
        sprintf
            "canonical-evidence verify: %s (commit=%s tree=%s path=%s)"
            status commitPrefix treePrefix r.Path
    else
        sprintf
            "canonical-evidence verify: %s (%s) commit=%s tree=%s path=%s"
            status reasons commitPrefix treePrefix r.Path

let private renderDependencyVerifySummary (r: DependencyVerifyOutcome) : string =
    let status =
        match r.Failure with
        | None -> "PASS"
        | Some _ -> "FAIL"
    let reasons =
        match r.Failure with
        | Some f -> dependencyVerifyFailureToString f
        | None -> ""
    let commitPrefix =
        match r.Evidence with
        | Some e when e.TestedCommitOid.Length >= 12 -> e.TestedCommitOid.Substring(0, 12)
        | _ -> "?"
    let treePrefix =
        match r.Evidence with
        | Some e when e.TestedTreeOid.Length >= 12 -> e.TestedTreeOid.Substring(0, 12)
        | _ -> "?"
    if String.IsNullOrEmpty reasons then
        sprintf
            "canonical-evidence verify: %s (commit=%s tree=%s path=%s)"
            status commitPrefix treePrefix r.Path
    else
        sprintf
            "canonical-evidence verify: %s (%s) commit=%s tree=%s path=%s"
            status reasons commitPrefix treePrefix r.Path

// -----------------------------------------------------------------------------
// Production runners (delegate to the existing CORRECTION01 surface).
// The dependency-driven entry points below are the canonical test
// seam; production callers use these wrappers.
// -----------------------------------------------------------------------------

let runRegenerate (repoRoot: string) (outputPath: string) (baselineCommit: string) (scopeDeclaration: string option) : int =
    let effectiveScope = resolveScopeDeclaration repoRoot scopeDeclaration
    if String.IsNullOrEmpty effectiveScope then
        stderr.WriteLine "canonical-evidence regenerate: FAIL (no scope declaration; supply --scope-declaration or create .factory/active-scope.json)"
        ExitCode.operationalError
    else
        match generate repoRoot baselineCommit effectiveScope with
        | Result.Error failure ->
            stderr.WriteLine(sprintf "canonical-evidence regenerate: FAIL (%s)" (generateFailureToString failure))
            ExitCode.operationalError
        | Result.Ok evidence ->
            let outcome = tryWriteAtomic outputPath evidence
            if not outcome.Success then
                let reason =
                    match outcome.Failure with
                    | Some f -> writeFailureToString f
                    | None -> "unknown"
                stderr.WriteLine(sprintf "canonical-evidence regenerate: FAIL (%s)" reason)
                ExitCode.operationalError
            else
                let overall = statusToken evidence.OverallStatus
                let verdict = if overall = "pass" then ExitCode.pass else ExitCode.policyFailure
                stdout.WriteLine(renderRegenerateSummary evidence outputPath outcome.CanonicalSha256)
                // Re-verify the freshly written bytes to confirm the
                // artifact on disk is what the producer intended.
                let verifyOutcome = verify outputPath repoRoot
                match verifyOutcome.Failure with
                | Some f ->
                    stderr.WriteLine(sprintf "canonical-evidence regenerate: FAIL (%s)" (verifyFailureToString f))
                    ExitCode.operationalError
                | None ->
                    if overall = "pass" then ExitCode.pass
                    else ExitCode.policyFailure

let runVerify (repoRoot: string) (inputPath: string) (_scopeDeclaration: string option) : int =
    let result = verify inputPath repoRoot
    let verdict =
        match result.Failure with
        | Some _ -> ExitCode.policyFailure
        | None -> ExitCode.pass
    let text = renderLegacyVerifySummary result
    match result.Failure with
    | Some _ -> stderr.WriteLine(text)
    | None -> stdout.WriteLine(text)
    verdict

// -----------------------------------------------------------------------------
// Dependency-driven runners
// -----------------------------------------------------------------------------

let internal runRegenerateWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (repoRoot: string)
    (outputPath: string)
    (baselineCommit: string)
    (scopeDeclaration: string option)
    : int =
    let effectiveScope = resolveScopeDeclaration repoRoot scopeDeclaration
    if String.IsNullOrEmpty effectiveScope then
        stderr.WriteLine "canonical-evidence regenerate: FAIL (no scope declaration; supply --scope-declaration or create .factory/active-scope.json)"
        ExitCode.operationalError
    else
        match regenerateWithDependencies deps repoRoot baselineCommit effectiveScope with
        | Result.Error failure ->
            stderr.WriteLine(sprintf "canonical-evidence regenerate: FAIL (%s)" (regenerateFailureToString failure))
            ExitCode.operationalError
        | Result.Ok evidence ->
            let writeOutcome = writeArtifactWithDependencies deps outputPath evidence
            if not writeOutcome.Success then
                let reason =
                    match writeOutcome.Failure with
                    | Some f -> sprintf "%s:%s" f.Reason f.Detail
                    | None -> "unknown"
                stderr.WriteLine(sprintf "canonical-evidence regenerate: FAIL (%s)" reason)
                ExitCode.operationalError
            else
                let overall = statusToken evidence.OverallStatus
                let verdict = if overall = "pass" then ExitCode.pass else ExitCode.policyFailure
                stdout.WriteLine(renderRegenerateSummary evidence outputPath writeOutcome.CanonicalSha256)
                let verifyOutcome = verifyWithDependencies deps outputPath repoRoot
                match verifyOutcome.Failure with
                | Some f ->
                    stderr.WriteLine(sprintf "canonical-evidence regenerate: FAIL (%s)" (dependencyVerifyFailureToString f))
                    ExitCode.operationalError
                | None ->
                    if overall = "pass" then ExitCode.pass
                    else ExitCode.policyFailure

let internal runVerifyWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (repoRoot: string)
    (inputPath: string)
    (scopeDeclaration: string option)
    : int =
    let result = verifyWithDependencies deps inputPath repoRoot
    let verdict =
        match result.Failure with
        | Some _ -> ExitCode.policyFailure
        | None -> ExitCode.pass
    let text = renderDependencyVerifySummary result
    match result.Failure with
    | Some _ -> stderr.WriteLine(text)
    | None -> stdout.WriteLine(text)
    verdict

// -----------------------------------------------------------------------------
// Dependency-driven CLI dispatcher
// -----------------------------------------------------------------------------

let internal runCliWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (argv: string list)
    : int =
    match parse argv with
    | Ok HelpCmd ->
        stdout.WriteLine(helpText ())
        ExitCode.pass
    | Ok(RegenerateCmd (repoRoot, outputPath, baselineCommit, scopeDeclaration)) ->
        runRegenerateWithDependencies deps repoRoot outputPath baselineCommit scopeDeclaration
    | Ok(VerifyCmd (repoRoot, inputPath, scopeDeclaration)) ->
        runVerifyWithDependencies deps repoRoot inputPath scopeDeclaration
    | Result.Error msg ->
        stderr.WriteLine(sprintf "error: %s" msg)
        stderr.WriteLine(helpText ())
        ExitCode.operationalError

let run (argv: string list) : int =
    let deps = productionDependencies ()
    runCliWithDependencies deps argv
