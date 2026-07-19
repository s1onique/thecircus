module Circus.DevHost.ShellProfile

open System
open System.IO

open Domain
open Circus.DevHost.Adapters

/// The marker pair used to bracket the managed block inside profile files.
let beginMarker = "# BEGIN CIRCUS DEVHOST"
let endMarker = "# END CIRCUS DEVHOST"

/// The managed block to inject inside the markers. The block calls
/// `circus-dev env --shell <bash|zsh>` once per shell session.
let renderBlock (binaryPath: string) (shell: Shell) : string =
    let shellName = shell.ShellName
    let bin = "'" + binaryPath.Replace("'", "'\\''") + "'"

    "# "
    + shellName
    + " managed block\n"
    + beginMarker
    + "\n"
    + "if [ -x "
    + bin
    + " ]; then\n"
    + "    eval \"$("
    + bin
    + " env --shell "
    + shellName
    + ")\"\n"
    + "fi\n"
    + endMarker
    + "\n"

/// Result of trying to apply a profile update.
type ApplyProfileResult =
    | Appended
    | ReplacedExisting
    | NoChangeNeeded
    | DuplicateBlocks of int
    | MalformedProfile of DevHostFailure
    | WriteError of DevHostFailure

/// Pure helper used by tests to locate managed blocks inside an existing
/// profile body.
let private markerCounts (content: string) : int * int =
    content.Split([| '\n' |])
    |> Array.fold
        (fun (begins, ends) line ->
            match line.Trim() with
            | marker when marker = beginMarker -> begins + 1, ends
            | marker when marker = endMarker -> begins, ends + 1
            | _ -> begins, ends)
        (0, 0)

let countMarkerPairs (content: string) : int =
    let begins, ends = markerCounts content
    min begins ends

/// Decide what `applyProfile` must do based on the current contents.
type ProfileDecision =
    | Create
    | Append
    | ReplaceExisting
    | Skip
    | FailAmbiguousDuplicate of int
    | FailMalformed

/// Locate a managed block and return the line index range. The pair is
/// inclusive of both markers.
let findBlockRange (text: string) : (int * int) option =
    let lines = text.Split([| '\n' |])
    let mutable beginLine = -1
    let mutable endLine = -1

    for i in 0 .. lines.Length - 1 do
        let trimmed = lines.[i].Trim()

        if trimmed = beginMarker && beginLine < 0 then
            beginLine <- i
        elif trimmed = endMarker && beginLine >= 0 && endLine < 0 then
            endLine <- i

    if beginLine >= 0 && endLine >= 0 && endLine >= beginLine then
        Some(beginLine, endLine)
    else
        None

let decide (existing: string option) (alwaysUpdate: bool) : ProfileDecision =
    match existing with
    | None -> Create
    | Some body ->
        let begins, ends = markerCounts body

        if begins = 0 && ends = 0 then
            Append
        elif begins <> ends || findBlockRange body |> Option.isNone then
            FailMalformed
        elif begins > 1 then
            FailAmbiguousDuplicate begins
        elif alwaysUpdate then
            ReplaceExisting
        else
            Skip

/// Replace the existing managed block in `text` with `newBlock`.
let swapBlock (text: string) (newBlock: string) : string =
    match findBlockRange text with
    | None -> text
    | Some(start, finish) ->
        let lines = text.Split([| '\n' |])
        let prefix = lines |> Array.take start
        let suffix = lines |> Array.skip (finish + 1)
        let prefixStr = String.concat "\n" prefix
        let suffixStr = String.concat "\n" suffix
        let sepLeft = if System.String.IsNullOrEmpty prefixStr then "" else "\n"
        let sepRight = if System.String.IsNullOrEmpty suffixStr then "" else "\n"
        prefixStr + sepLeft + newBlock + sepRight + suffixStr

/// Apply the profile update. Idempotent and fail-closed.
let applyProfile (fs: IFilesystem) (profilePath: string) (block: string) (alwaysUpdate: bool) : ApplyProfileResult =
    try
        let dir = Path.GetDirectoryName profilePath

        if not (String.IsNullOrEmpty dir) && not (fs.IsDirectory dir) then
            fs.CreateDirectory dir

        let existing: string option =
            if fs.IsFile profilePath then
                Some(fs.ReadAllText profilePath)
            else
                None

        match decide existing alwaysUpdate with
        | Create ->
            fs.WriteAllText(
                profilePath,
                (if existing.IsSome then existing.Value else "")
                + (if existing.IsSome && not (existing.Value.EndsWith "\n") then
                       "\n"
                   else
                       "")
                + block
            )

            Appended
        | Append ->
            let current = if existing.IsSome then existing.Value else ""
            let withBlank = if current.EndsWith "\n" then current else current + "\n"
            fs.WriteAllText(profilePath, withBlank + block)
            Appended
        | ReplaceExisting ->
            let body = existing |> Option.defaultValue ""
            fs.WriteAllText(profilePath, swapBlock body block)
            ReplacedExisting
        | Skip -> NoChangeNeeded
        | FailAmbiguousDuplicate n -> DuplicateBlocks n
        | FailMalformed -> MalformedProfile(ProfileUpdateFailure(profilePath, "malformed managed marker block"))
    with ex ->
        WriteError(ProfileUpdateFailure(profilePath, ex.Message))

/// Build the binary path string used by the managed block. Always
/// `~/.local/share/circus-dev/bin/circus-dev` unless `CIRCUS_TOOL_ROOT`
/// was overridden by the operator.
let defaultBinaryPath (toolRoot: string) : string =
    Path.Combine(toolRoot, "bin", "circus-dev")

/// Path to the per-shell profile used by `circus-dev install-shell-hook`.
let profilePathFor (env: IEnvironment) (shell: Shell) : string =
    let home: string =
        match env.GetEnv "HOME" with
        | Some h -> h
        | None -> ""

    match shell with
    | Bash -> Path.Combine(home, ".bashrc")
    | Zsh -> Path.Combine(home, ".zshrc")
