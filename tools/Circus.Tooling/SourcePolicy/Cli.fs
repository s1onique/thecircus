module Circus.Tooling.SourcePolicy.Cli

open System
open System.IO

open Circus.Tooling.SourcePolicy.Paths
open Circus.Tooling.SourcePolicy.Verification
open Circus.Tooling.SourcePolicy.Domain
open Circus.Tooling.SourcePolicy.JsonReport
open Circus.Tooling.SourcePolicy.HumanReport

module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

type Command =
    | VerifyCmd of fmt: string
    | InventoryCmd of fmt: string
    | ExplainCmd of path: string * fmt: string
    | HelpCmd
    | VersionCmd

let helpText () : string =
    "circus-tooling source-policy — ML-only source policy verifier\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling source-policy verify [--format human|json]\n"
    + "  circus-tooling source-policy inventory [--format human|json]\n"
    + "  circus-tooling source-policy explain <path> [--format human|json]\n"
    + "  circus-tooling source-policy help\n"

let parseFormat (value: string) : Result<string, string> =
    match value with
    | "human" | "json" -> Ok value
    | _ -> Error(sprintf "unknown --format value: %s" value)

let rec skipFlags (args: string list) (fmt: string) : Result<string, string> =
    match args with
    | [] -> Ok fmt
    | "--format" :: value :: rest ->
        match parseFormat value with
        | Ok f -> skipFlags rest f
        | Error e -> Error e
    | other :: _ -> Error(sprintf "unexpected argument: %s" other)

let parse (argv: string list) : Result<Command, string> =
    match argv with
    | [] | [ "help" ] | [ "-h" ] | [ "--help" ] -> Ok HelpCmd
    | [ "version" ] -> Ok VersionCmd
    | [ "verify" ] -> Ok(VerifyCmd "human")
    | "verify" :: rest ->
        match skipFlags rest "human" with
        | Ok f -> Ok(VerifyCmd f)
        | Error e -> Error e
    | [ "inventory" ] -> Ok(InventoryCmd "human")
    | "inventory" :: rest ->
        match skipFlags rest "human" with
        | Ok f -> Ok(InventoryCmd f)
        | Error e -> Error e
    | "explain" :: path :: rest ->
        match skipFlags rest "human" with
        | Ok f -> Ok(ExplainCmd(path, f))
        | Error e -> Error e
    | _ -> Error "usage: source-policy {verify|inventory|explain <path>|help|version}"

let resolveRepoRoot () : Result<string, string> =
    match Inventory.discoverRoot Environment.CurrentDirectory with
    | Inventory.Root r -> Ok r
    | Inventory.NotARepository ->
        Error(sprintf "not in a Git repository (cwd=%s)" Environment.CurrentDirectory)

let private emit (format: string) (text: string) : unit =
    stdout.WriteLine text

let runVerify (fmt: string) (repoRoot: string) : int =
    let cfg = defaultConfig repoRoot
    let outcome = verify cfg
    if fmt = "json" then emit fmt (renderVerify outcome)
    else emit fmt (renderVerify outcome)
    if List.isEmpty outcome.Findings then ExitCode.pass
    else ExitCode.policyFailure

let runInventory (fmt: string) (repoRoot: string) : int =
    let outcome = verify (defaultConfig repoRoot)
    emit fmt (JsonReport.renderVerify outcome)
    if List.isEmpty outcome.Findings then ExitCode.pass else ExitCode.policyFailure

let runExplain (path: string) (fmt: string) (repoRoot: string) : int =
    emit fmt (JsonReport.renderOperationalError "not_implemented" "explain command is not implemented in this build")
    ExitCode.operationalError
