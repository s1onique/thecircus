module Circus.DevHost.Domain

/// Exit classification for the entire `circus-dev` process.
/// Used by every command to map the typed `CheckResult` summary into an
/// OS-level exit code. Never over-load a Boolean to mean the same thing.
type ExitClass =
    | Success
    | CapabilityFailure
    | ContractError

/// The host architecture we currently support. The F# program fails closed
/// when the host reports anything else.
type SupportedArchitecture =
    | LinuxX64

/// Linux distributions we treat as supported for the bootstrap cycle.
type SupportedDistribution =
    | Ubuntu
    | Debian
    | LinuxMint
    | OtherLinux

/// Shells we target for env quoting and profile block management.
type Shell =
    | Bash
    | Zsh

    member this.ShellName : string =
        match this with
        | Bash -> "bash"
        | Zsh -> "zsh"

/// Every executable or runtime the devhost manages or inspects.
type Tool =
    | DotNetSdk
    | DotNetInteractive // F# Interactive
    | Node
    | Npm
    | ElmCompiler
    | ElmTest
    | PolicyPython
    | PyYaml
    | Actionlint
    | ShellCheck
    | Docker
    | DockerBuildx
    | DockerCompose
    | Leamas
    | PsqlClient
    | Make

/// A semantic version string the program has accepted as well-formed.
type ToolVersion = private { Value: string }

module ToolVersion =
    let tryParse (raw: string) : ToolVersion option =
        if System.Text.RegularExpressions.Regex.IsMatch(
            raw,
            "^[0-9]+\.[0-9]+\.[0-9]+([-+][A-Za-z0-9.\-]+)?$"
          )
        then Some { Value = raw }
        else None

    let unsafeParse (raw: string) : ToolVersion =
        match tryParse raw with
        | Some v -> v
        | None -> failwithf "Malformed semantic version '%s'" raw

    let parse (raw: string) : Result<ToolVersion, string> =
        match tryParse raw with
        | Some v -> Ok v
        | None -> Error(sprintf "Malformed semantic version '%s'" raw)

    let value (v: ToolVersion) : string = v.Value

    /// Strip a leading 'v' (actionlint and dotnet-install use 'v' prefixes).
    let normalize (raw: string) : string =
        if raw.StartsWith "v" then raw.Substring 1 else raw

/// Cryptographic hash algorithm names accepted by download integrity
/// verification.
type IntegrityAlgorithm =
    | Sha256
    | Sha512

/// Verification state for a single check. Required for the unified
/// human/JSON doctor output and for the bootstrap reconciliation logic.
type CheckStatus =
    | Passed
    | Failed of DevHostFailure
    | Skipped of string

and DevHostFailure =
    | UnsupportedOperatingSystem of string
    | UnsupportedArchitecture of string
    | RepositoryIdentityFailure of string
    | RepositoryDirty
    | MissingAuthorityFile of string
    | MalformedAuthorityFile of string
    | MissingTool of Tool
    | WrongToolVersion of Tool * expected:string * actual:string
    | DownloadFailure of uri:string * detail:string
    | IntegrityFailure of path:string * expected:string * actual:string
    | ExtractionFailure of path:string * detail:string
    | ProcessStartFailure of command:string * detail:string
    | ProcessExitFailure of command:string * exitCode:int * stderr:string
    | DockerPermissionDenied
    | ProfileUpdateFailure of path:string * detail:string
    | VerificationFailure of string
    | LayoutWrong of path:string * detail:string

/// Result of a single named check. Caller decides what detail to surface.
type CheckResult = {
    Name: string
    Status: CheckStatus
    Detail: string option
}

/// Aggregate outcome of a list of checks. Used by both Doctor and Verify.
type AggregateOutcome = {
    Checks: CheckResult list
}

/// True when every check Passed or was intentionally Skipped.
let aggregateIsPassing (checks: CheckResult list) : bool =
    checks
    |> List.forall (fun c ->
        match c.Status with
        | Passed -> true
        | Skipped _ -> true
        | Failed _ -> false)

/// Render a failure as a stable string. We deliberately use a pattern match
/// (not Exception text) so contract violations are unambiguous.
let renderFailure (f: DevHostFailure) : string =
    match f with
    | UnsupportedOperatingSystem os -> sprintf "unsupported OS '%s'" os
    | UnsupportedArchitecture arch -> sprintf "unsupported architecture '%s'" arch
    | RepositoryIdentityFailure detail -> sprintf "repository identity: %s" detail
    | RepositoryDirty -> "repository working tree is dirty"
    | MissingAuthorityFile p -> sprintf "missing authority file '%s'" p
    | MalformedAuthorityFile p -> sprintf "malformed authority file '%s'" p
    | MissingTool t -> sprintf "missing tool '%A'" t
    | WrongToolVersion (t, expected, actual) ->
        sprintf "tool '%A' wrong version: expected %s, got %s" t expected actual
    | DownloadFailure (uri, detail) -> sprintf "download '%s' failed: %s" uri detail
    | IntegrityFailure (path, expected, actual) ->
        sprintf "integrity mismatch for '%s': expected %s, got %s" path expected actual
    | ExtractionFailure (path, detail) -> sprintf "extract '%s' failed: %s" path detail
    | ProcessStartFailure (cmd, detail) ->
        sprintf "cannot start process '%s': %s" cmd detail
    | ProcessExitFailure (cmd, code, stderr) ->
        sprintf "process '%s' exit %d: %s" cmd code stderr
    | DockerPermissionDenied -> "docker daemon not directly accessible"
    | ProfileUpdateFailure (path, detail) ->
        sprintf "profile '%s' update failed: %s" path detail
    | VerificationFailure detail -> sprintf "verification failed: %s" detail
    | LayoutWrong (path, detail) ->
        sprintf "unexpected layout at '%s': %s" path detail

/// Map an `AggregateOutcome` to its `ExitClass`.
let classify (outcome: AggregateOutcome) : ExitClass =
    let anyContract =
        outcome.Checks
        |> List.exists (fun c ->
            match c.Status with
            | Failed f ->
                match f with
                | UnsupportedOperatingSystem _
                | UnsupportedArchitecture _
                | RepositoryIdentityFailure _
                | MissingAuthorityFile _
                | MalformedAuthorityFile _
                | ProfileUpdateFailure _ -> true
                | _ -> false
            | Passed
            | Skipped _ -> false)

    if anyContract then ContractError
    else if aggregateIsPassing outcome.Checks then Success
    else CapabilityFailure
