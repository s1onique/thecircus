module Circus.Tooling.SourcePolicy.Verification

open System.IO

open Circus.Tooling.SourcePolicy.Language
open Circus.Tooling.SourcePolicy.Paths
open Circus.Tooling.SourcePolicy.Inventory
open Circus.Tooling.SourcePolicy.NulInventory
open Circus.Tooling.SourcePolicy.Classifier
open Circus.Tooling.SourcePolicy.Domain
open Circus.Tooling.SourcePolicy.Shebang
open Circus.Tooling.SourcePolicy.ShellPolicy
open Circus.Tooling.SourcePolicy.InvocationPolicy
open Circus.Tooling.SourcePolicy.Baseline

type VerifyConfig = {
    RepoRoot: string
    BaselinePath: string
    RejectBaselineExpansion: bool
    DetectUnknownExes: bool
}

let defaultConfig (repoRoot: string) : VerifyConfig =
    { RepoRoot = repoRoot
      BaselinePath = Path.Combine(repoRoot, "factory", "source-policy-baseline.csv")
      RejectBaselineExpansion = true
      DetectUnknownExes = true }

// PerFile defined in Domain

let private safeRead (repoRoot: string) (relativePath: string) : string option =
    try
        let full = safeResolve repoRoot relativePath
        if File.Exists full then Some(File.ReadAllText full) else None
    with
    | _ -> None

let private recordSourceForbidden (findings: ResizeArray<Finding>) (path: string) (detail: string) (rule: string) (actual: string) =
    findings.Add(
        { Path = path; Code = ForbiddenSourceLanguage; Line = None
          Detail = detail; Rule = rule
          Expected = Some "F# or Elm"; Actual = Some actual })

let evaluateFile (cfg: VerifyConfig) (relativePath: string) : Domain.PerFile =
    let cls =
        try Classifier.classify cfg.RepoRoot relativePath
        with
        | _ ->
            { Path = relativePath
              Category = FileCategory.Unknown
              Language = SourceLanguage.Unknown
              Shebang = "none"
              PhysicalLines = 0
              Sha256 = ""
              Executable = false }

    let findings = ResizeArray<Finding>()

    if isAbsolute relativePath then
        findings.Add(
            { Path = relativePath; Code = RepositoryBoundaryEscape; Line = None
              Detail = "inventory entry is an absolute path"
              Rule = "repository/no-absolute-paths"
              Expected = Some "repository-relative"; Actual = Some "absolute" })
    elif (containsParentTraversal relativePath) <> "" then
        findings.Add(
            { Path = relativePath; Code = RepositoryBoundaryEscape; Line = None
              Detail = "inventory entry escapes via parent traversal"
              Rule = "repository/no-parent-traversal"
              Expected = Some "no '..'"; Actual = Some ".." })

    let shebang = Shebang.classify (safeRead cfg.RepoRoot relativePath |> Option.defaultValue "")

    match cls.Category with
    | FileCategory.FSharpProduction -> ()
    | FileCategory.FSharpScript ->
        recordSourceForbidden findings relativePath "F# script (.fsx) must live under tools/experiments/" "fsharp/fsx-only-in-examples" relativePath
    | FileCategory.ElmSource -> ()
    | FileCategory.ShellScript ->
        match safeRead cfg.RepoRoot relativePath with
        | Some text -> findings.AddRange(ShellPolicy.evaluate relativePath text shebang)
        | None ->
            findings.Add(
                { Path = relativePath; Code = FileReadFailure; Line = None
                  Detail = "cannot read shell script"
                  Rule = "file/readable"
                  Expected = Some "readable"; Actual = Some "unreadable" })
    | FileCategory.StageZeroLauncher ->
        match safeRead cfg.RepoRoot relativePath with
        | Some text -> findings.AddRange(ShellPolicy.evaluateStageZero relativePath text shebang)
        | None ->
            findings.Add(
                { Path = relativePath; Code = FileReadFailure; Line = None
                  Detail = "cannot read stage-zero launcher"
                  Rule = "file/readable"
                  Expected = Some "readable"; Actual = Some "unreadable" })
    | FileCategory.Makefile | FileCategory.Dockerfile | FileCategory.NixFlake | FileCategory.WorkflowYaml ->
        match safeRead cfg.RepoRoot relativePath with
        | Some text -> findings.AddRange(InvocationPolicy.evaluate relativePath text)
        | None -> ()
    | FileCategory.DeclarativeDoc -> ()
    | FileCategory.Unknown ->
        match cls.Language with
        | SourceLanguage.Python ->
            recordSourceForbidden findings relativePath "Python source is not an approved implementation language" "source/no-python" "Python"
        | SourceLanguage.Go ->
            recordSourceForbidden findings relativePath "Go source is not an approved implementation language" "source/no-go" "Go"
        | SourceLanguage.JavaScript ->
            recordSourceForbidden findings relativePath "JavaScript source is not an approved implementation language" "source/no-javascript" "JavaScript"
        | SourceLanguage.TypeScript ->
            recordSourceForbidden findings relativePath "TypeScript source is not an approved implementation language" "source/no-typescript" "TypeScript"
        | SourceLanguage.Ruby ->
            recordSourceForbidden findings relativePath "Ruby source is not an approved implementation language" "source/no-ruby" "Ruby"
        | SourceLanguage.Perl ->
            recordSourceForbidden findings relativePath "Perl source is not an approved implementation language" "source/no-perl" "Perl"
        | SourceLanguage.Php ->
            recordSourceForbidden findings relativePath "PHP source is not an approved implementation language" "source/no-php" "Php"
        | SourceLanguage.Lua ->
            recordSourceForbidden findings relativePath "Lua source is not an approved implementation language" "source/no-lua" "Lua"
        | SourceLanguage.PowerShell ->
            recordSourceForbidden findings relativePath "PowerShell source is not an approved implementation language" "source/no-powershell" "PowerShell"
        | SourceLanguage.Haskell ->
            recordSourceForbidden findings relativePath "Haskell source is not an approved implementation language" "source/no-haskell" "Haskell"
        | SourceLanguage.OCaml ->
            recordSourceForbidden findings relativePath "OCaml source is not an approved implementation language" "source/no-ocaml" "OCaml"
        | SourceLanguage.Unknown when cfg.DetectUnknownExes ->
            match safeRead cfg.RepoRoot relativePath with
            | Some _ ->
                match shebang with
                | Shebang.ShebangForbidden(interp, _) ->
                    findings.Add(
                        { Path = relativePath; Code = UnknownExecutableShebang; Line = None
                          Detail = sprintf "extensionless file with forbidden interpreter shebang: %s" interp
                          Rule = "shebang/no-forbidden-interpreter"
                          Expected = Some "#!/bin/sh"; Actual = Some interp })
                | Shebang.ShebangBash _ ->
                    findings.Add(
                        { Path = relativePath; Code = UnknownExecutableShebang; Line = None
                          Detail = "extensionless file with Bash shebang"
                          Rule = "shebang/no-bash"
                          Expected = Some "#!/bin/sh"; Actual = Some "bash" })
                | Shebang.ShebangUnknown _ ->
                    findings.Add(
                        { Path = relativePath; Code = UnregisteredExecutableText; Line = None
                          Detail = "extensionless file with unknown shebang"
                          Rule = "shebang/must-be-registered"
                          Expected = Some "#!/bin/sh"; Actual = Some (Shebang.tag shebang) })
                | Shebang.ShebangEnv _ ->
                    findings.Add(
                        { Path = relativePath; Code = UnregisteredExecutableText; Line = None
                          Detail = "extensionless file with env shebang"
                          Rule = "shebang/must-be-registered"
                          Expected = Some "#!/bin/sh"; Actual = Some (Shebang.tag shebang) })
                | _ -> ()
            | None -> ()
        | _ -> ()

    { Classification = cls; Findings = findings |> Seq.toList } : Domain.PerFile

let private isPolicyRelevant (p: string) : bool =
    let ext = extensionOf p
    let name = filenameOf p
    isApprovedFSharpExtension ext
    || isApprovedElmExtension ext
    || isForbiddenExtension ext
    || isApprovedShellExtension ext
    || isForbiddenGoModuleFile name
    || name = "Makefile" || name.StartsWith "Makefile."
    || ext = ".mk"
    || name = "Dockerfile" || name.StartsWith "Dockerfile."
    || name = "flake.nix"
    || ext = ".yml" || ext = ".yaml"
    || (ext = "" && not (p.Contains "/" = false))
    || ext = ".md"


let verify (cfg: VerifyConfig) : VerificationOutcome =
    match Inventory.enumerate cfg.RepoRoot with
    | InventoryDiagnostic d ->
        { RepositoryRoot = cfg.RepoRoot
          FilesExamined = 0
          BaselineEntries = 0
          Findings =
            [ { Path = "<repo>"; Code = GitInventoryFailure; Line = None
                Detail = NulInventory.renderDiagnostic d; Rule = "git/ls-files"
                Expected = Some "ok"; Actual = Some "failed" } ] }
    | InventoryEntries rawEntries ->
        match Inventory.splitTrackedUntracked cfg.RepoRoot rawEntries with
        | InventoryDiagnostic d ->
            { RepositoryRoot = cfg.RepoRoot
              FilesExamined = 0
              BaselineEntries = 0
              Findings =
                [ { Path = "<repo>"; Code = GitInventoryFailure; Line = None
                    Detail = NulInventory.renderDiagnostic d; Rule = "git/ls-files"
                    Expected = Some "ok"; Actual = Some "failed" } ] }
        | InventoryEntries entries ->
            let baselineLoad : Baseline.LoadResult = Baseline.load cfg.RepoRoot
            let baselineEntries, baselineFindings =
                match baselineLoad with
                | Loaded es -> es, []
                | Missing -> [], []
                | Malformed detail ->
                    [], [ { Path = cfg.BaselinePath; Code = BaselineMalformed; Line = None
                            Detail = detail; Rule = "baseline/parseable"
                            Expected = Some Baseline.Header; Actual = Some "malformed" } ]

            let relevant =
                entries
                |> List.filter (fun e ->
                    let p = e.RelativePath
                    isPolicyRelevant p && not (isVendoredElmPath p))

            let perFile : Domain.PerFile list = relevant |> List.map (fun e -> evaluateFile cfg e.RelativePath)

            let afterBaseline =
                perFile
                |> List.map (fun (pf: Domain.PerFile) ->
                    let hasOversized = pf.Findings |> List.exists (fun f -> f.Code = OversizedShell)
                    if not hasOversized then pf
                    else
                        match Baseline.matchEntry baselineEntries pf.Classification.Path pf.Classification.Sha256 pf.Classification.PhysicalLines with
                        | MatchOk ->
                            { pf with Findings = pf.Findings |> List.filter (fun f -> f.Code <> OversizedShell) }
                        | DigestMismatch(expected, actual) ->
                            { pf with
                                Findings =
                                    pf.Findings
                                    @ [ { Path = pf.Classification.Path; Code = BaselineDigestMismatch; Line = None
                                          Detail = sprintf "baseline digest mismatch (expected %s, got %s)" expected actual
                                          Rule = "baseline/digest-matches-file"
                                          Expected = Some expected; Actual = Some actual } ] }
                        | MeasurementMismatch(expected, actual) ->
                            { pf with
                                Findings =
                                    pf.Findings
                                    @ [ { Path = pf.Classification.Path; Code = BaselineMeasurementMismatch; Line = None
                                          Detail = sprintf "baseline line-count mismatch (expected %d, got %d)" expected actual
                                          Rule = "baseline/measurement-matches-file"
                                          Expected = Some(string expected); Actual = Some(string actual) } ] }
                        | MissingForCurrentFile -> pf)

            let currentViolationSet =
                afterBaseline
                |> List.filter (fun (pf: Domain.PerFile) ->
                    pf.Findings |> List.exists (fun f -> f.Code = OversizedShell))
                |> List.map (fun (pf: Domain.PerFile) -> pf.Classification.Path)
                |> Set.ofList

            let present =
                entries
                |> List.map (fun e -> e.RelativePath)
                |> Set.ofList

            let allObserved = Set.union currentViolationSet present
            let staleFindings = Baseline.staleFindings baselineEntries allObserved
            let expansionFindings =
                if cfg.RejectBaselineExpansion then Baseline.expansionFindings baselineEntries allObserved
                else []

            let baselineF = baselineFindings |> Seq.toList
            let staleF = staleFindings |> Seq.toList
            let expansionF = expansionFindings |> Seq.toList
            let collected =
                afterBaseline
                |> List.map (fun (pf: Domain.PerFile) -> pf.Findings)
                |> List.concat
            let preFindings = collected @ baselineF @ staleF @ expansionF
            let allFindings =
                preFindings
                |> List.sortBy (fun (f: Domain.Finding) ->
                    (f.Path, f.Code.AsTag, (match f.Line with | Some n -> n | None -> 0), f.Detail))

            { RepositoryRoot = cfg.RepoRoot
              FilesExamined = relevant.Length
              BaselineEntries = List.length baselineEntries
              Findings = allFindings }
