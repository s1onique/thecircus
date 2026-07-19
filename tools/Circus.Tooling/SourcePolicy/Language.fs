module Circus.Tooling.SourcePolicy.Language

type SourceLanguage =
    | FSharp | Elm | PosixShell | Python | Go | JavaScript | TypeScript
    | Ruby | Perl | Php | Lua | PowerShell | Haskell | OCaml
    | UnknownExecutable | Declarative | Unknown

    member this.AsTag =
        match this with
        | FSharp -> "fsharp" | Elm -> "elm" | PosixShell -> "posix-shell"
        | Python -> "python" | Go -> "go" | JavaScript -> "javascript"
        | TypeScript -> "typescript" | Ruby -> "ruby" | Perl -> "perl"
        | Php -> "php" | Lua -> "lua" | PowerShell -> "powershell"
        | Haskell -> "haskell" | OCaml -> "ocaml"
        | UnknownExecutable -> "unknown-executable" | Declarative -> "declarative"
        | Unknown -> "unknown"

type FileCategory =
    | FSharpProduction | FSharpScript | ElmSource | ShellScript | StageZeroLauncher
    | Makefile | Dockerfile | WorkflowYaml | NixFlake | DeclarativeDoc | Unknown

    member this.AsTag =
        match this with
        | FSharpProduction -> "fsharp-production" | FSharpScript -> "fsharp-script"
        | ElmSource -> "elm-source" | ShellScript -> "shell-script"
        | StageZeroLauncher -> "stage-zero-launcher" | Makefile -> "makefile"
        | Dockerfile -> "dockerfile" | WorkflowYaml -> "workflow-yaml"
        | NixFlake -> "nix-flake" | DeclarativeDoc -> "declarative-doc"
        | Unknown -> "unknown"

type ViolationCode =
    | ForbiddenSourceLanguage | ForbiddenInterpreterInvocation
    | OversizedShell | NonPosixShell | ShellContainsDomainLogic
    | UnknownExecutableShebang | UnregisteredExecutableText
    | BaselineMissing | BaselineMalformed | BaselineStale
    | BaselineDigestMismatch | BaselineMeasurementMismatch
    | BaselineExpansion | RepositoryBoundaryEscape
    | FileReadFailure | GitInventoryFailure

    member this.AsTag =
        match this with
        | ForbiddenSourceLanguage -> "forbidden_source_language"
        | ForbiddenInterpreterInvocation -> "forbidden_interpreter_invocation"
        | OversizedShell -> "oversized_shell" | NonPosixShell -> "non_posix_shell"
        | ShellContainsDomainLogic -> "shell_contains_domain_logic"
        | UnknownExecutableShebang -> "unknown_executable_shebang"
        | UnregisteredExecutableText -> "unregistered_executable_text"
        | BaselineMissing -> "baseline_missing" | BaselineMalformed -> "baseline_malformed"
        | BaselineStale -> "baseline_stale" | BaselineDigestMismatch -> "baseline_digest_mismatch"
        | BaselineMeasurementMismatch -> "baseline_measurement_mismatch"
        | BaselineExpansion -> "baseline_expansion" | RepositoryBoundaryEscape -> "repository_boundary_escape"
        | FileReadFailure -> "file_read_failure" | GitInventoryFailure -> "git_inventory_failure"

let isForbiddenExtension (ext: string) =
    let forbidden = [".py"; ".pyw"; ".go"; ".js"; ".cjs"; ".mjs"; ".jsx"; ".ts"; ".cts"; ".mts"; ".tsx"; ".rb"; ".pl"; ".pm"; ".php"; ".lua"; ".ps1"; ".psm1"; ".hs"; ".lhs"; ".ml"; ".mli"]
    List.contains ext forbidden

let isApprovedFSharpExtension (ext: string) =
    (List.contains ext [".fs"; ".fsi"; ".fsproj"])

let isApprovedElmExtension (ext: string) =
    ext = ".elm"

let isApprovedShellExtension (ext: string) =
    ext = ".sh"

let isForbiddenGoModuleFile (filename: string) =
    filename = "go.mod" || filename = "go.sum"
