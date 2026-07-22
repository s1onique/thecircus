module Circus.Tooling.NoForcePush.SurfaceInventory

open System
open System.IO
open System.Text

/// Failures that can occur during inventory operations.
type InventoryError =
    | GitListFilesFailed of detail: string
    | MalformedCsvRow of rowNumber: int * detail: string
    | DuplicatePath of path: string
    | MissingInventoryFile of path: string
    | FileMissing of path: string
    | SymlinkEscapes of path: string

/// Parse a surface kind string to the discriminated union.
let parseSurfaceKind (s: string) : Types.SurfaceKind option =
    match s.Trim().ToLowerInvariant() with
    | "executable" -> Some Types.Executable
    | "workflow" -> Some Types.Workflow
    | "make" -> Some Types.Make
    | "container" -> Some Types.Container
    | "agent-executable" -> Some Types.AgentExecutable
    | _ -> None

/// Parse a parser kind string to the discriminated union.
let parseParserKind (s: string) : Types.ParserKind option =
    match s.Trim().ToLowerInvariant() with
    | "shell" -> Some Types.ParserKind.Shell
    | "make" -> Some Types.ParserKind.Make
    | "yaml-run" -> Some Types.ParserKind.YamlRun
    | "dockerfile" -> Some Types.ParserKind.Dockerfile
    | "plaintext-command" -> Some Types.ParserKind.PlaintextCommand
    | _ -> None

/// Parse an authority level string.
let parseAuthority (s: string) : Types.AuthorityLevel option =
    match s.Trim().ToLowerInvariant() with
    | "repository" -> Some Types.Repository
    | "github" -> Some Types.GitHub
    | _ -> None

/// Parse a single CSV row into a SurfaceEntry.
let parseRow (rowNumber: int) (fields: string array) : Result<Types.SurfaceEntry, InventoryError> =
    if Array.length fields <> 5 then
        Error(MalformedCsvRow(rowNumber, sprintf "expected 5 fields, got %d" (Array.length fields)))
    else
        let path = fields.[0].Trim()
        let surfaceKind = parseSurfaceKind fields.[1]
        let parserKind = parseParserKind fields.[2]
        let authority = parseAuthority fields.[3]
        let reason = fields.[4].Trim()

        match surfaceKind, parserKind, authority with
        | Some sk, Some pk, Some auth ->
            if String.IsNullOrWhiteSpace path then
                Error(MalformedCsvRow(rowNumber, "path is empty"))
            else
                Ok
                    { Types.SurfaceEntry.Path = path
                      Types.SurfaceEntry.SurfaceKind = sk
                      Types.SurfaceEntry.ParserKind = pk
                      Types.SurfaceEntry.Authority = auth
                      Types.SurfaceEntry.Reason = reason }
        | _ ->
            Error(
                MalformedCsvRow(
                    rowNumber,
                    sprintf
                        "invalid surface_kind='%s', parser_kind='%s', authority='%s'"
                        fields.[1]
                        fields.[2]
                        fields.[3]
                )
            )

/// Parse the CSV content into surface entries.
let parseCsv (content: string) : Result<Types.SurfaceEntry list, InventoryError> =
    let lines = content.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)

    if Array.isEmpty lines then
        Ok []
    else
        // Skip header row
        let dataLines = lines |> Array.skip 1
        let mutable errors = []
        let mutable entries = []
        let mutable seenPaths = Set.empty

        for i, line in dataLines |> Array.indexed do
            let rowNumber = i + 2 // 1-based, accounting for header
            let fields = line.Split(',')

            match parseRow rowNumber fields with
            | Ok entry ->
                if Set.contains entry.Path seenPaths then
                    errors <- MalformedCsvRow(rowNumber, sprintf "duplicate path: %s" entry.Path) :: errors
                else
                    seenPaths <- Set.add entry.Path seenPaths
                    entries <- entry :: entries
            | Error e -> errors <- e :: errors

        match errors with
        | [] -> Ok(List.rev entries)
        | first :: _ -> Error(first)

/// Validate that all inventory paths exist, are tracked, and are safe.
/// Uses path-relative containment, not raw string-prefix comparison.
let validatePaths
    (root: string)
    (entries: Types.SurfaceEntry list)
    (trackedFiles: Set<string>)
    : Result<unit, InventoryError> =
    let mutable errors = []
    let rootFull = Path.GetFullPath(root)

    for entry in entries do
        // 1. Check path exists
        let fullPath = Path.Combine(root, entry.Path)

        if not (File.Exists fullPath) && not (Directory.Exists fullPath) then
            errors <- FileMissing entry.Path :: errors

        // 2. Check path is tracked
        if not (Set.contains entry.Path trackedFiles) then
            errors <- FileMissing entry.Path :: errors

        // 3. Check for symlinks that escape the repository (fail-closed)
        if File.Exists fullPath then
            try
                let info = FileInfo(fullPath)

                if info.Attributes.HasFlag(FileAttributes.ReparsePoint) then
                    // Symlink found - resolve it safely and check containment
                    let target = info.LinkTarget

                    if not (String.IsNullOrEmpty target) then
                        let targetFull =
                            if Path.IsPathRooted target then
                                Path.GetFullPath(target)
                            else
                                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath), target))

                        // Use path-relative containment check
                        let targetUri = Uri(targetFull + Path.DirectorySeparatorChar.ToString())
                        let rootUri = Uri(rootFull + Path.DirectorySeparatorChar.ToString())

                        if
                            not (
                                targetUri.IsBaseOf(rootUri)
                                || targetUri.ToString().StartsWith(rootUri.ToString())
                            )
                        then
                            errors <- SymlinkEscapes entry.Path :: errors
                    else
                        // Unresolved symlink - fail closed
                        errors <- SymlinkEscapes entry.Path :: errors
            with _ ->
                // Symlink resolution failed - fail closed
                errors <- SymlinkEscapes entry.Path :: errors

    match errors with
    | [] -> Ok()
    | first :: _ -> Error(first)

/// Get tracked files from git ls-files -z, returning a set of paths.
let getTrackedFiles (root: string) : Result<Set<string>, InventoryError> =
    try
        let psi = System.Diagnostics.ProcessStartInfo()
        psi.FileName <- "git"
        psi.Arguments <- "ls-files -z"
        psi.WorkingDirectory <- root
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true

        use proc = System.Diagnostics.Process.Start(psi)
        let output = proc.StandardOutput.ReadToEnd()
        let error = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            Error(GitListFilesFailed(sprintf "git ls-files -z failed: %s" error))
        else
            // Parse null-terminated output
            let nullChar = char 0

            let files =
                output.Split([| nullChar |], StringSplitOptions.RemoveEmptyEntries)
                |> Set.ofArray

            Ok files
    with ex ->
        Error(GitListFilesFailed(sprintf "failed to run git ls-files: %s" ex.Message))

/// Read and validate the surface inventory from the factory directory.
let readInventory (root: string) : Result<Types.SurfaceEntry list, InventoryError> =
    let inventoryPath = Path.Combine(root, "factory", "no-force-push-surfaces.csv")

    if not (File.Exists inventoryPath) then
        Error(MissingInventoryFile inventoryPath)
    else
        try
            let content = File.ReadAllText(inventoryPath, Encoding.UTF8)

            match parseCsv content with
            | Error e -> Error e
            | Ok entries ->
                match getTrackedFiles root with
                | Error e -> Error e
                | Ok trackedFiles ->
                    match validatePaths root entries trackedFiles with
                    | Error e -> Error e
                    | Ok() -> Ok entries
        with ex ->
            Error(MalformedCsvRow(0, sprintf "failed to read inventory: %s" ex.Message))

/// Find executable files in the tracked set that are not in the inventory.
let findUnclassifiedExecutables
    (root: string)
    (trackedFiles: Set<string>)
    (inventory: Types.SurfaceEntry list)
    : string list =
    let inventoryPaths = inventory |> List.map (fun e -> e.Path) |> Set.ofList

    trackedFiles
    |> Set.toList
    |> List.filter (fun path ->
        let fullPath = Path.Combine(root, path)

        if File.Exists fullPath then
            try
                let info = FileInfo(fullPath)
                // Check if executable (Unix-style)
                let isExecutable =
                    info.UnixFileMode.HasFlag(UnixFileMode.UserExecute)
                    || info.UnixFileMode.HasFlag(UnixFileMode.GroupExecute)
                    || info.UnixFileMode.HasFlag(UnixFileMode.OtherExecute)

                // Check if it's a script with shebang
                let hasShebang =
                    try
                        use sr = new StreamReader(fullPath)
                        let first = sr.ReadLine()
                        not (isNull first) && first.StartsWith("#!")
                    with _ ->
                        false

                // It's a governed surface if executable or has shebang
                // and not already in inventory
                (isExecutable || hasShebang) && not (Set.contains path inventoryPaths)
            with _ ->
                false
        else
            false)
