module Circus.Tooling.SourcePolicy.InvocationPolicy

open System.Text.RegularExpressions

open Circus.Tooling.SourcePolicy.Language
open Circus.Tooling.SourcePolicy.Domain
open Circus.Tooling.SourcePolicy.Paths

let ForbiddenInterpreterCommands : string list =
    [ "python"; "python2"; "python3"; "pypy"; "pypy3"
      "go"; "node"; "deno"; "bun"; "ts-node"
      "ruby"; "perl"; "php"; "lua"
      "pwsh"; "powershell"
      "runhaskell"; "ghc"; "ocaml" ]

let private interpreterPatterns : (string * Regex) list =
    let re s = Regex(s, RegexOptions.Compiled ||| RegexOptions.Multiline)
    [ "python", re "(^|[\\s;&|]|[/])python[23]?\\b"
      "pypy", re "(^|[\\s;&|]|[/])pypy[3]?\\b"
      "go", re "(^|[\\s;&|])go\\s+(run|build|test|generate)\\b"
      "node", re "(^|[\\s;&|]|[/])(node|deno|bun)\\b"
      "ts-node", re "(^|[\\s;&|])(ts-node|npx\\s+ts-node)\\b"
      "ruby", re "(^|[\\s;&|]|[/])ruby\\b"
      "perl", re "(^|[\\s;&|]|[/])perl\\b"
      "php", re "(^|[\\s;&|]|[/])php\\b"
      "lua", re "(^|[\\s;&|]|[/])lua\\b"
      "powershell", re "(^|[\\s;&|]|[/])(pwsh|powershell)\\b"
      "haskell", re "(^|[\\s;&|])(runhaskell|ghc|stack)\\b"
      "ocaml", re "(^|[\\s;&|])(ocaml|dune)\\b" ]

let isOperational (relativePath: string) : bool =
    let n = filenameOf relativePath
    let e = extensionOf relativePath
    n = "Makefile" || n.StartsWith("Makefile.")
    || n = "Dockerfile" || n.StartsWith("Dockerfile.")
    || e = ".mk" || e = ".sh"
    || e = ".yml" || e = ".yaml"
    || n = "go.mod" || n = "go.sum"
    || n = "flake.nix"

let private looksLikeComment (line: string) : bool =
    let trimmed = line.TrimStart()
    trimmed.StartsWith "#"
    || trimmed.StartsWith "//"
    || trimmed.StartsWith "--"

let evaluate (relativePath: string) (text: string) : Finding list =
    if not (isOperational relativePath) then []
    elif isVendoredElmPath relativePath then []
    else
        let findings = ResizeArray<Finding>()
        let lines = text.Split('\n')
        for i in 0 .. lines.Length - 1 do
            let line = lines.[i]
            if not (looksLikeComment line) then
                for command, regex in interpreterPatterns do
                    if regex.IsMatch line then
                        findings.Add
                            { Path = relativePath; Code = ForbiddenInterpreterInvocation
                              Line = Some(i + 1)
                              Detail = sprintf "forbidden interpreter invocation: %s" command
                              Rule = sprintf "interpreter/no-%s" command
                              Expected = Some "<not-invoked>"
                              Actual = Some command }
        findings |> Seq.toList
