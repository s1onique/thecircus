module Circus.Tooling.SourcePolicy.Cli

open System
open System.IO

open Circus.Tooling.SourcePolicy.Paths
open Circus.Tooling.SourcePolicy.Inventory
open Circus.Tooling.SourcePolicy.Classifier
open Circus.Tooling.SourcePolicy.Verification
open Circus.Tooling.SourcePolicy.Domain
open Circus.Tooling.SourcePolicy.JsonReport
open Circus.Tooling.SourcePolicy.HumanReport
open Circus.Tooling.SourcePolicy.ContainerPolicy
open Circus.Tooling.SourcePolicy.GateSummary
open Circus.Tooling.SourcePolicy.GateSummaryVerify

module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

type Command =
    | VerifyCmd of fmt: string
    | ContainerPolicyCmd of fmt: string
    | GateSummaryRegenerateCmd
    | GateSummaryVerifyCmd
    | GateRunCmd
    | HelpCmd
    | VersionCmd

let helpText () : string =
    "circus-tooling — F# implementation policy verifier (source-policy, container-policy, gate-summary)\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling source-policy verify [--format human|json]\n"
    + "  circus-tooling container-policy verify\n"
    + "  circus-tooling gate-summary regenerate\n"
    + "  circus-tooling gate-summary verify\n"
    + "  circus-tooling gate run\n"
    + "  circus-tooling help\n"
    + "  circus-tooling version\n"

let private parseFormat (args: string list) : Result<string, string> =
    match args with
    | [] -> Ok "human"
    | [ "--format"; "human" ] -> Ok "human"
    | [ "--format"; "json" ] -> Ok "json"
    | [ "--format"; _ ] -> Error "format must be 'human' or 'json'"
    | _ -> Error "unrecognised arguments after subcommand"

let parse (argv: string list) : Result<Command, string> =
    match argv with
    | [] | [ "help" ] | [ "-h" ] | [ "--help" ] -> Ok HelpCmd
    | [ "version" ] -> Ok VersionCmd
    | "source-policy" :: "verify" :: rest ->
        match parseFormat rest with
        | Ok fmt -> Ok(VerifyCmd fmt)
        | Error e -> Error e
    | "container-policy" :: "verify" :: rest ->
        match parseFormat rest with
        | Ok fmt -> Ok(ContainerPolicyCmd fmt)
        | Error e -> Error e
    | "gate-summary" :: "regenerate" :: [] -> Ok GateSummaryRegenerateCmd
    | "gate-summary" :: "verify" :: [] -> Ok GateSummaryVerifyCmd
    | "gate" :: "run" :: [] -> Ok GateRunCmd
    | _ -> Error "usage: circus-tooling {source-policy verify|container-policy verify|gate-summary regenerate|gate-summary verify|gate run|help|version}"

let resolveRepoRoot () : Result<string, string> =
    match Inventory.discoverRoot Environment.CurrentDirectory with
    | Inventory.Root r -> Ok r
    | Inventory.NotARepository ->
        Error(sprintf "not in a Git repository (cwd=%s)" Environment.CurrentDirectory)

let runSourcePolicyVerify (repoRoot: string) : int =
    let cfg = defaultConfig repoRoot
    let outcome : VerificationOutcome = Verification.verify cfg
    stdout.WriteLine (HumanReport.renderVerify outcome)
    if List.isEmpty outcome.Findings then ExitCode.pass
    else ExitCode.policyFailure

let runContainerPolicy (repoRoot: string) : int =
    ContainerPolicy.runVerify repoRoot

let runGateSummaryRegenerate (repoRoot: string) : int =
    GateSummary.runRegenerate repoRoot

let runGateSummaryVerify (repoRoot: string) : int =
    let path = Path.Combine(repoRoot, ".factory", "gate-summary.json")
    let expected = GateSummaryVerify.tryReadExpectedTreeOid repoRoot
    GateSummaryVerify.runVerify path expected

/// ``gate run`` is the single canonical entry point that invokes every
/// canonical local check exactly once, regenerates the gate-summary
/// artefact, **always** validates the artefact structurally and
/// against the committed tree, and combines the three independent
/// verdicts:
///
///   - the regeneration operational result (could the producer run?);
///   - the structural validation result (does the artefact satisfy
///     the Leamas v1 wire contract?);
///   - the per-check verdict (did every canonical check pass?).
///
/// The runner always runs validation, even when the regeneration
/// step observed failed checks; the canonical contract requires the
/// artefact to be valid before its content is consumed.
let runGate (repoRoot: string) : int =
    let path = Path.Combine(repoRoot, ".factory", "gate-summary.json")

    // Step 1: regenerate the artefact.  The runner inside
    // ``regenerate`` invokes each canonical check exactly once.
    // A failed check returns exit 1; an operational error returns
    // exit 2.  We capture the code but **always** proceed to
    // validation: a successfully-written failed artefact must still
    // satisfy the wire contract before the gate reports PASS.
    let regenCode = GateSummary.runRegenerate repoRoot

    // Step 2: validate the artefact structurally and against
    // HEAD^{tree}.  ``runVerify`` returns 0 (all pass), 1 (valid
    // with failed checks), or 2 (malformed / binding unverifiable).
    let verifyCode = runGateSummaryVerify repoRoot

    // Step 3: combine the verdicts.  The most severe code wins,
    // matching the conventional Unix severity ordering
    // (operational=2 > structural/checks=1 > pass=0).
    let combined =
        if regenCode = ExitCode.operationalError || verifyCode = ExitCode.operationalError then
            ExitCode.operationalError
        else if regenCode = ExitCode.policyFailure || verifyCode = ExitCode.policyFailure then
            ExitCode.policyFailure
        else
            ExitCode.pass

    if combined = ExitCode.pass then
        stdout.WriteLine "gate run: PASS"
    else if combined = ExitCode.policyFailure then
        stderr.WriteLine(sprintf "gate run: FAIL (regen=%d verify=%d, see gate-summary.json)" regenCode verifyCode)
    else
        stderr.WriteLine(sprintf "gate run: FAIL (operational; regen=%d verify=%d)" regenCode verifyCode)
    combined
