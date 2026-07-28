module Circus.Tooling.CanonicalEvidence.Cli

// =============================================================================
// Canonical evidence – CLI dispatcher
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION02
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01
//
// This module implements the canonical evidence CLI with five commands:
//
//   provide    - Generate canonical execution evidence for an explicit Git subject.
//                This is the authoritative provider command that requires an
//                explicit complete subject commit OID.
//
//   regenerate - Regenerate legacy canonical-evidence projections from the current
//                checkout. This command does NOT produce subject-bound closure
//                evidence and is retained for backward compatibility only.
//
//   verify     - Verify a canonical evidence artifact against current repository state.
//
//   inventory  - List all provider-generated evidence records in the evidence root.
//
//   show       - Display a specific evidence record by ID.
//
// Exit codes:
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

// Evidence root directory under factory
[<Literal>]
let CanonicalEvidenceRoot = "factory/evidence/canonical-executions/v1"

/// Try to create directory if it doesn't exist. Returns Error with message on failure.
let private tryCreateDirectoryIfNeeded (path: string) : Result<unit, string> =
    if Directory.Exists path then Ok ()
    else
        try
            Directory.CreateDirectory path |> ignore
            Ok ()
        with ex -> Error ex.Message

type Command =
    | ProvideCmd of repoRoot: string * subjectOid: string * outputDirectory: string option * scopeDeclaration: string option
    | RegenerateCmd of repoRoot: string * outputPath: string * baselineCommit: string * scopeDeclaration: string option
    | VerifyCmd of repoRoot: string * inputPath: string * scopeDeclaration: string option
    | InventoryCmd of evidenceRoot: string
    | ShowCmd of evidenceRoot: string * evidenceId: string
    | HelpCmd

let helpText () : string =
    "canonical-evidence — repository-owned canonical evidence provider\n"
    + "\n"
    + "Commands:\n"
    + "  provide       Generate canonical execution evidence for an explicit Git subject.\n"
    + "                This is the authoritative provider command that requires an\n"
    + "                explicit complete subject commit OID.\n"
    + "  regenerate    Regenerate legacy canonical-evidence projections from the current\n"
    + "                checkout. This command does NOT produce subject-bound closure evidence.\n"
    + "  verify        Verify a canonical evidence artifact against current repository state.\n"
    + "  inventory     List all provider-generated evidence records in the evidence root.\n"
    + "  show          Display a specific evidence record by ID.\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling canonical-evidence provide \\\n"
    + "    --repo-root <path> --subject <full-commit-oid> [--output-directory <path>] [--scope-declaration <path>]\n"
    + "  circus-tooling canonical-evidence regenerate \\\n"
    + "    --repo-root <path> --output <path> --baseline-commit <oid> [--scope-declaration <path>]\n"
    + "  circus-tooling canonical-evidence verify \\\n"
    + "    --repo-root <path> --input <path> [--scope-declaration <path>]\n"
    + "  circus-tooling canonical-evidence inventory [--evidence-root <path>]\n"
    + "  circus-tooling canonical-evidence show <evidence-id> [--evidence-root <path>]\n"
    + "  circus-tooling canonical-evidence help\n"

let private consumeFlag (flag: string) (args: string list) : Result<string * string list, string> =
    match args with
    | v :: rest -> Ok (v, rest)
    | _ -> Error (sprintf "missing value for %s" flag)

let private parse (argv: string list) : Result<Command, string> =
    match argv with
    | [] | [ "help" ] | [ "-h" ] | [ "--help" ] -> Ok HelpCmd
    | [ "provide" ] ->
        Error "provide requires --repo-root and --subject"
    | [ "regenerate" ] ->
        Error "regenerate requires --repo-root, --output, and --baseline-commit"
    | [ "verify" ] ->
        Error "verify requires --repo-root and --input"
    | [ "inventory" ] ->
        Ok(InventoryCmd CanonicalEvidenceRoot)
    | [ "show" ] ->
        Error "show requires an evidence-id argument"
    | [ "show"; evidenceId ] ->
        Ok(ShowCmd (CanonicalEvidenceRoot, evidenceId))
    | "provide" :: rest ->
        let mutable repoRoot : string option = None
        let mutable subjectOid : string option = None
        let mutable outputDir : string option = None
        let mutable scopeDecl : string option = None
        let mutable remaining = rest
        let mutable bad = false
        while not bad && not (List.isEmpty remaining) do
            match remaining with
            | "--repo-root" :: t ->
                match consumeFlag "--repo-root" t with
                | Ok (v, r) -> repoRoot <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | "--subject" :: t ->
                match consumeFlag "--subject" t with
                | Ok (v, r) -> subjectOid <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | "--output-directory" :: t ->
                match consumeFlag "--output-directory" t with
                | Ok (v, r) -> outputDir <- Some v; remaining <- r
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
            match repoRoot, subjectOid with
            | Some r, Some s -> Ok(ProvideCmd (r, s, outputDir, scopeDecl))
            | _ -> Error "provide requires --repo-root and --subject"
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
    | "inventory" :: rest ->
        let mutable evidenceRoot = CanonicalEvidenceRoot
        let mutable remaining = rest
        let mutable bad = false
        while not bad && not (List.isEmpty remaining) do
            match remaining with
            | "--evidence-root" :: t ->
                match consumeFlag "--evidence-root" t with
                | Ok (v, r) -> evidenceRoot <- v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | unknown :: _ ->
                bad <- true
                stderr.WriteLine(sprintf "error: unrecognised argument: %s" unknown)
            | [] -> ()
        if bad then Error "argument parse failed"
        else Ok(InventoryCmd evidenceRoot)
    | "show" :: evidenceId :: rest ->
        let mutable evidenceRoot = CanonicalEvidenceRoot
        let mutable remaining = rest
        let mutable bad = false
        while not bad && not (List.isEmpty remaining) do
            match remaining with
            | "--evidence-root" :: t ->
                match consumeFlag "--evidence-root" t with
                | Ok (v, r) -> evidenceRoot <- v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | unknown :: _ ->
                bad <- true
                stderr.WriteLine(sprintf "error: unrecognised argument: %s" unknown)
            | [] -> ()
        if bad then Error "argument parse failed"
        else Ok(ShowCmd (evidenceRoot, evidenceId))
    | _ ->
        Error "usage: canonical-evidence {provide|regenerate|verify|inventory|show|help}"

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
    match scopeDeclaration with
    | None ->
        stderr.WriteLine "canonical-evidence regenerate: FAIL (legacy entry point requires --scope-declaration)"
        ExitCode.operationalError
    | Some effectiveScope ->
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

let runVerify (repoRoot: string) (inputPath: string) (scopeDeclaration: string option) : int =
    let deps = productionDependencies ()
    let result = verifyWithDependencies deps inputPath repoRoot scopeDeclaration
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
// Dependency-driven runners
// -----------------------------------------------------------------------------

let internal runRegenerateWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (repoRoot: string)
    (outputPath: string)
    (baselineCommit: string)
    (scopeDeclaration: string option)
    : int =
    match regenerateWithDependencies deps repoRoot baselineCommit scopeDeclaration with
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
                let verifyOutcome = verifyWithDependencies deps outputPath repoRoot scopeDeclaration
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
    let result = verifyWithDependencies deps inputPath repoRoot scopeDeclaration
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
// Provide command (subject-bound evidence generation)
// -----------------------------------------------------------------------------

let private renderProvideSummary (e: CanonicalEvidence) (written: string) : string =
    sprintf
        "canonical-evidence provide: bytes_sha256=%s schema_version=%d provider=%s/%s overall=%s commit=%s tree=%s checks=%d"
        written
        e.SchemaVersion
        e.ProviderName
        e.ProviderVersion
        (statusToken e.OverallStatus)
        (if e.TestedCommitOid.Length >= 12 then e.TestedCommitOid.Substring(0, 12) else e.TestedCommitOid)
        (if e.TestedTreeOid.Length >= 12 then e.TestedTreeOid.Substring(0, 12) else e.TestedTreeOid)
        (List.length e.Checks)

let internal runProvide
    (deps: CanonicalEvidenceDependencies)
    (repoRoot: string)
    (subjectOid: string)
    (outputDirectory: string option)
    (scopeDeclaration: string option)
    : int =
    // Determine output path
    let evidenceRoot =
        match outputDirectory with
        | Some dir -> dir
        | None -> CanonicalEvidenceRoot

    // Ensure the output directory exists (early return on failure)
    match tryCreateDirectoryIfNeeded evidenceRoot with
    | Error msg ->
        stderr.WriteLine(sprintf "canonical-evidence provide: FAIL (cannot create directory: %s)" msg)
        ExitCode.operationalError
    | Ok () ->
        // Continue with evidence generation
        let artifactPath = Path.Combine(evidenceRoot, "canonical-evidence.json")

        // Generate evidence for the subject commit
        match provideWithDependencies deps repoRoot subjectOid scopeDeclaration with
        | Result.Error failure ->
            stderr.WriteLine(sprintf "canonical-evidence provide: FAIL (%s)" (provideFailureToString failure))
            ExitCode.operationalError
        | Result.Ok evidence ->
            // Write atomically
            let writeOutcome = writeArtifactWithDependencies deps artifactPath evidence
            if not writeOutcome.Success then
                let reason =
                    match writeOutcome.Failure with
                    | Some f -> sprintf "%s:%s" f.Reason f.Detail
                    | None -> "unknown"
                stderr.WriteLine(sprintf "canonical-evidence provide: FAIL (write failed: %s)" reason)
                ExitCode.operationalError
            else
                let overall = statusToken evidence.OverallStatus
                let verdict = if overall = "pass" then ExitCode.pass else ExitCode.policyFailure
                stdout.WriteLine(renderProvideSummary evidence writeOutcome.CanonicalSha256)

                // Append to records.jsonl for inventory
                let recordsPath = Path.Combine(evidenceRoot, "records.jsonl")
                let recordLine = renderWireJson evidence
                try
                    File.AppendAllText(recordsPath, recordLine + "\n")
                with ex ->
                    stderr.WriteLine(sprintf "canonical-evidence provide: WARNING (failed to update records: %s)" ex.Message)

                // Verify the freshly written bytes
                let verifyOutcome = verifyWithDependencies deps artifactPath repoRoot scopeDeclaration
                match verifyOutcome.Failure with
                | Some f ->
                    stderr.WriteLine(sprintf "canonical-evidence provide: FAIL (verify failed: %s)" (dependencyVerifyFailureToString f))
                    ExitCode.operationalError
                | None ->
                    if overall = "pass" then ExitCode.pass
                    else ExitCode.policyFailure

// -----------------------------------------------------------------------------
// Inventory command (read-only, never executes checks)
// -----------------------------------------------------------------------------

let internal runInventory (evidenceRoot: string) : int =
    let recordsPath = Path.Combine(evidenceRoot, "records.jsonl")
    if not (File.Exists recordsPath) then
        stdout.WriteLine(sprintf "canonical-evidence inventory: no records found at %s" evidenceRoot)
        stdout.WriteLine("No records.jsonl found. Run 'provide' first to generate evidence.")
        ExitCode.pass
    else
        try
            let lines = File.ReadAllLines recordsPath
            if lines.Length = 0 then
                stdout.WriteLine("canonical-evidence inventory: no records found")
                ExitCode.pass
            else
                stdout.WriteLine(sprintf "canonical-evidence inventory: %d record(s) found" lines.Length)
                stdout.WriteLine("")
                for line in lines do
                    match parseWireJson line with
                    | Result.Ok e ->
                        let commit =
                            if e.TestedCommitOid.Length >= 12 then e.TestedCommitOid.Substring(0, 12)
                            else e.TestedCommitOid
                        stdout.WriteLine(sprintf "  %s  commit=%s status=%s checks=%d"
                            (e.SemanticSha256.Substring(0, 8))
                            commit
                            (statusToken e.OverallStatus)
                            (List.length e.Checks))
                    | Result.Error _ ->
                        stdout.WriteLine(sprintf "  [malformed record]")
                stdout.WriteLine("")
                stdout.WriteLine(sprintf "Total: %d records" lines.Length)
                ExitCode.pass
        with ex ->
            stderr.WriteLine(sprintf "canonical-evidence inventory: FAIL (read error: %s)" ex.Message)
            ExitCode.operationalError

// -----------------------------------------------------------------------------
// Show command (read-only, never executes checks)
// -----------------------------------------------------------------------------

let internal runShow (evidenceRoot: string) (evidenceId: string) : int =
    let recordsPath = Path.Combine(evidenceRoot, "records.jsonl")
    if not (File.Exists recordsPath) then
        stderr.WriteLine(sprintf "canonical-evidence show: no records found at %s" evidenceRoot)
        ExitCode.operationalError
    else
        try
            let lines = File.ReadAllLines recordsPath
            let mutable found = false
            let mutable result = ExitCode.operationalError
            for line in lines do
                match parseWireJson line with
                | Result.Ok e ->
                    if e.SemanticSha256.StartsWith(evidenceId) || e.SemanticSha256 = evidenceId then
                        stdout.WriteLine(renderWireJson e)
                        found <- true
                        result <- ExitCode.pass
                | Result.Error _ -> ()
            if not found then
                stderr.WriteLine(sprintf "canonical-evidence show: evidence-id '%s' not found" evidenceId)
                ExitCode.policyFailure
            else
                result
        with ex ->
            stderr.WriteLine(sprintf "canonical-evidence show: FAIL (read error: %s)" ex.Message)
            ExitCode.operationalError

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
    | Ok(ProvideCmd (repoRoot, subjectOid, outputDir, scopeDeclaration)) ->
        runProvide deps repoRoot subjectOid outputDir scopeDeclaration
    | Ok(RegenerateCmd (repoRoot, outputPath, baselineCommit, scopeDeclaration)) ->
        runRegenerateWithDependencies deps repoRoot outputPath baselineCommit scopeDeclaration
    | Ok(VerifyCmd (repoRoot, inputPath, scopeDeclaration)) ->
        runVerifyWithDependencies deps repoRoot inputPath scopeDeclaration
    | Ok(InventoryCmd evidenceRoot) ->
        runInventory evidenceRoot
    | Ok(ShowCmd (evidenceRoot, evidenceId)) ->
        runShow evidenceRoot evidenceId
    | Result.Error msg ->
        stderr.WriteLine(sprintf "error: %s" msg)
        stderr.WriteLine(helpText ())
        ExitCode.operationalError

let run (argv: string list) : int =
    let deps = productionDependencies ()
    runCliWithDependencies deps argv
