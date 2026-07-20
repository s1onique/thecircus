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

module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

type Command =
    | VerifyCmd of fmt: string
    | ContainerPolicyCmd of fmt: string
    | GateSummaryCmd of fmt: string
    | HelpCmd
    | VersionCmd

let helpText () : string =
    "circus-tooling — F# implementation policy verifier (source-policy, container-policy, gate-summary)\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling source-policy verify\n"
    + "  circus-tooling container-policy verify\n"
    + "  circus-tooling gate-summary regenerate\n"
    + "  circus-tooling help\n"

let parse (argv: string list) : Result<Command, string> =
    match argv with
    | [] | [ "help" ] | [ "-h" ] | [ "--help" ] -> Ok HelpCmd
    | [ "version" ] -> Ok VersionCmd
    | [ "source-policy"; "verify" ] | [ "verify" ] -> Ok(VerifyCmd "human")
    | [ "container-policy"; "verify" ] | [ "container-policy" ] -> Ok(ContainerPolicyCmd "human")
    | [ "gate-summary"; "regenerate" ] | [ "gate-summary" ] -> Ok(GateSummaryCmd "human")
    | _ -> Error "usage: circus-tooling {source-policy verify|container-policy verify|gate-summary regenerate|help}"

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

let runGateSummary (repoRoot: string) : int =
    GateSummary.runRegenerate repoRoot
