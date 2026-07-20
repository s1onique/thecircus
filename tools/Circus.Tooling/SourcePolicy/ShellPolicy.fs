module Circus.Tooling.SourcePolicy.ShellPolicy

open System.Text.RegularExpressions

open Circus.Tooling.SourcePolicy.Language
open Circus.Tooling.SourcePolicy.Domain
open Circus.Tooling.SourcePolicy.Paths
open Circus.Tooling.SourcePolicy.Shebang

let MaxShellLines = 50

type AntiPattern =
    | BashArray | BashDoubleBracket | BashSource | BashEval
    | HeredocWithExec | JsonParse | YamlParse | TomlParse
    | XmlParse | SqlParse | RetryLoop | PackageResolve
    | DownloadAndPipe | DynConstruct | DomainFunction

    member this.AsTag =
        match this with
        | BashArray -> "bash-array" | BashDoubleBracket -> "bash-double-bracket"
        | BashSource -> "bash-source" | BashEval -> "bash-eval"
        | HeredocWithExec -> "heredoc-with-exec" | JsonParse -> "json-parse"
        | YamlParse -> "yaml-parse" | TomlParse -> "toml-parse"
        | XmlParse -> "xml-parse" | SqlParse -> "sql-parse"
        | RetryLoop -> "retry-loop" | PackageResolve -> "package-resolve"
        | DownloadAndPipe -> "download-and-pipe" | DynConstruct -> "dynamic-construct"
        | DomainFunction -> "domain-function"

let private patterns : (AntiPattern * Regex) list =
    let re s = Regex(s, RegexOptions.Compiled ||| RegexOptions.Multiline)
    [ BashArray, re "\\bdeclare\\s+-a\\b"
      BashDoubleBracket, re "\\[\\[[^]]*\\]\\]"
      BashSource, re "(^|[^A-Za-z0-9_])source\\b|\\.\\s+[/A-Za-z_][A-Za-z0-9_/.]*\\s*(#|$)"
      BashEval, re "(^|[^A-Za-z0-9_])eval\\b"
      HeredocWithExec, re "<<[^<]*\\$\\(.*\\)"
      JsonParse, re "\\bjq\\s+[a-z]|python[23]?\\s+-c.*json"
      YamlParse, re "\\byq\\b"
      TomlParse, re "\\btq\\b"
      XmlParse, re "\\bxmllint\\b"
      SqlParse, re "\\bpsql\\s+-[a-zA-Z]*[fF]\\b"
      RetryLoop, re "for\\s+.*\\battempt\\b|while\\s+.*\\battempt\\b"
      PackageResolve, re "\\bnpm\\s+(view|install)|dotnet\\s+add\\s+package|pip[3]?\\s+install\\b"
      DownloadAndPipe, re "curl\\b[^|]*\\|\\s*(bash|sh|zsh)\\b|wget\\b[^|]*\\|\\s*(bash|sh|zsh)\\b"
      DynConstruct, re "xargs\\b[^&]*\\bsh\\b"
      DomainFunction, re "^[a-zA-Z_][a-zA-Z0-9_]*\\s*\\(\\s*\\)\\s*\\{" ]

let private shebangFinding (path: string) (code: ViolationCode) (detail: string) (rule: string) (expected: string) (actual: string) : Finding =
    { Path = path
      Code = code
      Line = None
      Detail = detail
      Rule = rule
      Expected = Some expected
      Actual = Some actual }

let evaluate (path: string) (text: string) (shebang: ShebangClassification) : Finding list =
    let findings = ResizeArray<Finding>()

    match shebang with
    | ShebangMissing ->
        findings.Add(shebangFinding path NonPosixShell "shell script lacks a shebang" "shell/shebang-required" "#!/bin/sh" "<missing>")
    | ShebangBomRejected _ ->
        findings.Add(shebangFinding path NonPosixShell "shell script begins with a UTF-8 BOM" "shell/bom-rejected" "#!/bin/sh" "0xEF 0xBB 0xBF")
    | ShebangBash _ ->
        findings.Add(shebangFinding path NonPosixShell "shell script uses a Bash shebang" "shell/shebang-must-be-posix" "#!/bin/sh" "bash")
    | ShebangEnv (i, _) ->
        findings.Add(shebangFinding path NonPosixShell (sprintf "shell script uses /usr/bin/env %s" i) "shell/shebang-must-be-posix" "#!/bin/sh" (sprintf "/usr/bin/env %s" i))
    | ShebangForbidden (i, _) ->
        findings.Add(shebangFinding path NonPosixShell (sprintf "shell script uses forbidden interpreter %s" i) "shell/shebang-must-be-posix" "#!/bin/sh" i)
    | ShebangUnknown _ ->
        findings.Add(shebangFinding path UnknownExecutableShebang "shell script shebang is not a recognised interpreter" "shell/shebang-must-be-posix" "#!/bin/sh" "unrecognised")
    | ShebangPosixShell _ -> ()

    let lines = LineCounting.count (System.Text.Encoding.UTF8.GetBytes text)
    if lines > MaxShellLines then
        findings.Add(
            { Path = path; Code = OversizedShell; Line = None
              Detail = sprintf "shell script exceeds %d physical lines" MaxShellLines
              Rule = "shell/max-50-lines"
              Expected = Some (sprintf "<= %d" MaxShellLines)
              Actual = Some (string lines) })

    for pattern, regex in patterns do
        let m = regex.Match text
        if m.Success then
            let prefix = text.Substring(0, m.Index)
            let lineNumber = prefix.Split('\n').Length
            findings.Add(
                { Path = path; Code = ShellContainsDomainLogic; Line = Some lineNumber
                  Detail = sprintf "shell contains banned construct: %s" pattern.AsTag
                  Rule = sprintf "shell/no-%s" pattern.AsTag
                  Expected = Some "<absent>"
                  Actual = Some pattern.AsTag })

    findings |> Seq.toList

let evaluateStageZero (path: string) (text: string) (shebang: ShebangClassification) : Finding list =
    let findings = ResizeArray<Finding>()

    match shebang with
    | ShebangPosixShell _ -> ()
    | ShebangMissing ->
        findings.Add(shebangFinding path UnknownExecutableShebang "stage-zero launcher lacks a shebang" "stage-zero/shebang-required" "#!/bin/sh" "<missing>")
    | _ ->
        findings.Add(shebangFinding path NonPosixShell "stage-zero launcher must use #!/bin/sh" "stage-zero/shebang-must-be-posix" "#!/bin/sh" (Shebang.tag shebang))

    let lines = LineCounting.count (System.Text.Encoding.UTF8.GetBytes text)
    if lines > MaxShellLines then
        findings.Add(
            { Path = path; Code = OversizedShell; Line = None
              Detail = sprintf "stage-zero launcher exceeds %d physical lines" MaxShellLines
              Rule = "stage-zero/max-50-lines"
              Expected = Some (sprintf "<= %d" MaxShellLines)
              Actual = Some (string lines) })

    for pattern, regex in patterns do
        let m = regex.Match text
        if m.Success then
            let prefix = text.Substring(0, m.Index)
            let lineNumber = prefix.Split('\n').Length
            findings.Add(
                { Path = path; Code = ShellContainsDomainLogic; Line = Some lineNumber
                  Detail = sprintf "stage-zero launcher contains banned construct: %s" pattern.AsTag
                  Rule = sprintf "stage-zero/no-%s" pattern.AsTag
                  Expected = Some "<absent>"
                  Actual = Some pattern.AsTag })

    findings |> Seq.toList
