module Circus.Tooling.NoForcePush.Cli

open System
open System.IO

/// Exit codes for the no-force-push verifier.
module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

/// Command types for the no-force-push subsystem.
type Command =
    | VerifyCmd of format: string
    | PrePushCmd of repo: string * remoteName: string * remoteUrl: string
    | GitHubRulesCmd of repository: string * branch: string
    | HelpCmd

let helpText () : string =
    "no-force-push — Git history safety verifier\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling no-force-push verify [--format human|json]\n"
    + "  circus-tooling no-force-push pre-push --repo <path> --remote-name <name> --remote-url <url>\n"
    + "  circus-tooling no-force-push github-rules verify --repository <repo> --branch <branch>\n"
    + "  circus-tooling no-force-push help\n"

/// Parse the no-force-push subcommand.
let parse (argv: string list) : Result<Command, string> =
    match argv with
    | [] | [ "help" ] | [ "-h" ] | [ "--help" ] -> Ok HelpCmd
    | "verify" :: rest ->
        match rest with
        | [] -> Ok(VerifyCmd "human")
        | [ "--format"; "human" ] -> Ok(VerifyCmd "human")
        | [ "--format"; "json" ] -> Ok(VerifyCmd "json")
        | [ "--format"; _ ] -> Error "format must be 'human' or 'json'"
        | _ -> Error "unexpected arguments after 'verify'"
    
    | "pre-push" :: rest ->
        let rec parseArgs (args: string list) (repo: string option) (remoteName: string option) (remoteUrl: string option) =
            match args with
            | "--repo" :: path :: tail ->
                match repo with
                | None -> parseArgs tail (Some path) remoteName remoteUrl
                | Some _ -> Error "duplicate --repo"
            | "--remote-name" :: name :: tail ->
                match remoteName with
                | None -> parseArgs tail repo (Some name) remoteUrl
                | Some _ -> Error "duplicate --remote-name"
            | "--remote-url" :: url :: tail ->
                match remoteUrl with
                | None -> parseArgs tail repo remoteName (Some url)
                | Some _ -> Error "duplicate --remote-url"
            | [] ->
                match repo, remoteName, remoteUrl with
                | Some r, Some n, Some u -> Ok(PrePushCmd(r, n, u))
                | _ -> Error "missing required arguments for pre-push"
            | _ -> Error "unrecognized argument"
        
        parseArgs rest None None None
    
    | "github-rules" :: "verify" :: rest ->
        let rec parseArgs (args: string list) (repo: string option) (branch: string option) =
            match args with
            | "--repository" :: r :: tail ->
                match repo with
                | None -> parseArgs tail (Some r) branch
                | Some _ -> Error "duplicate --repository"
            | "--branch" :: b :: tail ->
                match branch with
                | None -> parseArgs tail repo (Some b)
                | Some _ -> Error "duplicate --branch"
            | [] ->
                match repo, branch with
                | Some r, Some b -> Ok(GitHubRulesCmd(r, b))
                | _ -> Error "missing required arguments for github-rules"
            | _ -> Error "unrecognized argument"
        
        parseArgs rest None None
    
    | _ -> Error "usage: circus-tooling no-force-push {verify|pre-push|github-rules|help}"

/// Resolve the repository root.
let resolveRepoRoot () : Result<string, string> =
    let cwd = Environment.CurrentDirectory
    let gitDir = Path.Combine(cwd, ".git")
    
    if Directory.Exists gitDir then
        Ok cwd
    else
        // Try parent directories
        let rec findRoot (path: string) : string option =
            let gitDir = Path.Combine(path, ".git")
            if Directory.Exists gitDir then Some path
            elif path = "/" then None
            else findRoot (Directory.GetParent(path).FullName)
        
        match findRoot cwd with
        | Some root -> Ok root
        | None -> Error(sprintf "not in a Git repository (cwd=%s)" cwd)

/// Run the static policy verify command.
let runVerify (repoRoot: string) (format: string) : int =
    let result = StaticPolicy.verify repoRoot
    
    match format.ToLowerInvariant() with
    | "json" ->
        stdout.WriteLine(Rendering.renderStaticPolicyJson result)
    | _ ->
        stdout.WriteLine(Rendering.renderStaticPolicyHuman result)
    
    if List.isEmpty result.Diagnostics && List.isEmpty result.OperationalErrors then
        ExitCode.pass
    elif not (List.isEmpty result.OperationalErrors) then
        ExitCode.operationalError
    else
        ExitCode.policyFailure

/// Run the pre-push command.
let runPrePush (repo: string) (remoteName: string) (remoteUrl: string) : int =
    PrePush.runPrePush repo remoteName remoteUrl

/// Run the GitHub rules verification command.
/// Currently disabled - GitHubRules module needs repair.
let runGitHubRules (repository: string) (branch: string) : int =
    eprintfn "GitHub rules verification is temporarily disabled."
    ExitCode.operationalError

/// Main entry point for the no-force-push CLI.
let run (argv: string list) : int =
    match parse argv with
    | Ok cmd ->
        match cmd with
        | HelpCmd ->
            stdout.WriteLine(helpText())
            ExitCode.pass
        
        | VerifyCmd format ->
            match resolveRepoRoot() with
            | Ok root -> runVerify root format
            | Error msg ->
                stderr.WriteLine(sprintf "Error: %s" msg)
                ExitCode.operationalError
        
        | PrePushCmd(repo, remoteName, remoteUrl) ->
            runPrePush repo remoteName remoteUrl
        
        | GitHubRulesCmd(repository, branch) ->
            runGitHubRules repository branch
    
    | Error msg ->
        stderr.WriteLine(sprintf "Error: %s" msg)
        stderr.WriteLine(helpText())
        ExitCode.operationalError
