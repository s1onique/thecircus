module Circus.Tooling.SourcePolicy.Shebang

type ShebangClassification =
    | ShebangPosixShell of original: string
    | ShebangBash of original: string
    | ShebangEnv of interpreter: string * original: string
    | ShebangForbidden of interpreter: string * original: string
    | ShebangUnknown of original: string
    | ShebangBomRejected of original: string
    | ShebangMissing

let classify (text: string) : ShebangClassification =
    if System.String.IsNullOrEmpty text then ShebangMissing
    else
        let bytes = System.Text.Encoding.UTF8.GetBytes text
        let len = min 512 bytes.Length
        let leading = System.Text.Encoding.UTF8.GetString(bytes, 0, len)
        if leading.Length > 0 && int leading.[0] = 0xFEFF then
            ShebangBomRejected leading
        elif leading.Length >= 2 && leading.[0] = '#' && leading.[1] = '!' then
            // Trim the first physical line so a trailing LF inside the
            // 512-byte prefix window does not break the head match.
            let newlineIdx = leading.IndexOf '\n'
            let shebangLine =
                if newlineIdx >= 0 then leading.Substring(0, newlineIdx)
                else leading
            let stripped = shebangLine.Substring 2
            let parts = stripped.Split([| ' ' |], System.StringSplitOptions.RemoveEmptyEntries)
            if parts.Length = 0 then ShebangUnknown leading
            else
                let head = parts.[0]
                let rest =
                    if parts.Length > 1
                    then parts |> Array.skip 1 |> Array.toList
                    else []
                if head = "/bin/sh" then ShebangPosixShell leading
                elif head = "/bin/bash" || head = "/usr/bin/bash" then ShebangBash leading
                elif head.StartsWith "/usr/bin/env" then
                    match rest with
                    | first :: _ ->
                        if first = "sh" then ShebangPosixShell leading
                        elif first = "bash" then ShebangBash leading
                        elif first = "python" || first = "python2" || first = "python3"
                          || first = "pypy" || first = "pypy3"
                          || first = "node" || first = "deno" || first = "bun" || first = "ts-node"
                          || first = "ruby" || first = "perl" || first = "php" || first = "lua"
                          || first = "pwsh" || first = "powershell"
                          || first = "runhaskell" || first = "ghc" || first = "stack"
                          || first = "ocaml" || first = "dune"
                          || first = "go" then ShebangForbidden(first, leading)
                        else ShebangUnknown leading
                    | [] -> ShebangUnknown leading
                elif head = "python" || head = "python2" || head = "python3"
                  || head = "pypy" || head = "pypy3"
                  || head = "node" || head = "deno" || head = "bun" || head = "ts-node"
                  || head = "ruby" || head = "perl" || head = "php" || head = "lua"
                  || head = "pwsh" || head = "powershell"
                  || head = "runhaskell" || head = "ghc" || head = "stack"
                  || head = "ocaml" || head = "dune"
                  || head = "go" then ShebangForbidden(head, leading)
                elif head = "/usr/bin/sh" then ShebangPosixShell leading
                else ShebangUnknown leading
        else ShebangMissing

let tag (c: ShebangClassification) : string =
    match c with
    | ShebangPosixShell _ -> "posix-shell"
    | ShebangBash _ -> "bash"
    | ShebangEnv (i, _) -> "env:" + i
    | ShebangForbidden (i, _) -> "forbidden:" + i
    | ShebangUnknown _ -> "unknown"
    | ShebangBomRejected _ -> "rejected-bom"
    | ShebangMissing -> "none"
