module Circus.Tooling.SourcePolicy.Inventory

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Text

open Paths

type GitRoot =
    | Root of string
    | NotARepository

let discoverRoot (startDir: string) : GitRoot =
    try
        let psi = ProcessStartInfo()
        psi.FileName <- "git"
        psi.WorkingDirectory <- startDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.ArgumentList.Add "rev-parse"
        psi.ArgumentList.Add "--show-toplevel"
        let proc = Process.Start psi
        if isNull proc then NotARepository
        else
            proc.WaitForExit()
            if proc.ExitCode = 0 then
                let output = (proc.StandardOutput.ReadToEnd()).Trim()
                if String.IsNullOrEmpty output then NotARepository
                else Root(toPosix output)
            else NotARepository
    with
    | _ -> NotARepository

let private runGit (repoRoot: string) (args: string list) : int * string * string =
    try
        let psi = ProcessStartInfo()
        psi.FileName <- "git"
        psi.WorkingDirectory <- repoRoot
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        for a in args do psi.ArgumentList.Add a
        let proc = Process.Start psi
        if isNull proc then -1, "", "Process.Start returned null"
        else
            let stdout = proc.StandardOutput.ReadToEnd()
            let stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()
            proc.ExitCode, stdout, stderr
    with
    | ex -> -1, "", ex.Message

type InventoryEntry = { RelativePath: string; IsTracked: bool }

let private unquote (rel: string) : string =
    if not (rel.StartsWith "\"") then rel
    elif not (rel.EndsWith "\"") then rel
    else
        let inner = rel.Substring(1, rel.Length - 2)
        let sb = StringBuilder()
        let mutable i = 0
        while i < inner.Length do
            let c = inner.[i]
            if c = '\\' && i + 1 < inner.Length then
                let n = inner.[i + 1]
                if n = 'n' then sb.Append('\n') |> ignore
                elif n = 't' then sb.Append('\t') |> ignore
                elif n = 'r' then sb.Append('\r') |> ignore
                elif n = '\\' then sb.Append('\\') |> ignore
                elif n = '"' then sb.Append('"') |> ignore
                else sb.Append(n) |> ignore
                i <- i + 2
            else
                sb.Append(c) |> ignore
                i <- i + 1
        sb.ToString()

let enumerate (repoRoot: string) : Result<InventoryEntry list, string> =
    let exit, stdout, stderr =
        runGit repoRoot [ "ls-files"; "--cached"; "--others"; "--exclude-standard"; "-z" ]
    if exit <> 0 then
        Error(sprintf "git ls-files failed (exit %d): %s" exit stderr)
    else
        let raw = stdout.Replace("\r\n", "\n")
        let pieces =
            raw.Split('\u0000')
            |> Array.filter (fun s -> not (String.IsNullOrEmpty s))
            |> Array.toList
        let entries =
            pieces
            |> List.map (fun rel -> unquote rel)
            |> List.map (fun rel -> { RelativePath = toPosix rel; IsTracked = true })
        Ok entries

let splitTrackedUntracked (repoRoot: string) (entries: InventoryEntry list) : Result<InventoryEntry list, string> =
    let exit, stdout, stderr =
        runGit repoRoot [ "ls-files"; "--cached"; "-z" ]
    if exit <> 0 then
        Error(sprintf "git ls-files --cached failed (exit %d): %s" exit stderr)
    else
        let tracked =
            stdout.Split('\u0000')
            |> Array.filter (fun s -> not (String.IsNullOrEmpty s))
            |> Array.map toPosix
            |> Set.ofArray
        Ok(entries |> List.map (fun e -> { e with IsTracked = tracked.Contains e.RelativePath }))
