module Circus.Tooling.NoForcePush.CommandLexer

open System
open System.IO
open System.Text
open System.Text.RegularExpressions

// ============================================================================
// Source location
// ============================================================================

/// Source location for a parsed token.
type SourceLocation =
    { Line: int
      Column: int
      AbsoluteOffset: int }

// ============================================================================
// Parsed argument
// ============================================================================

/// A parsed argument with its position.
type ParsedArgument =
    { Index: int
      Value: string
      IsQuoted: bool
      IsVariable: bool }

// ============================================================================
// Parsed command record (P0-3 requirement)
// ============================================================================

/// A parsed command with full provenance.
type ParsedCommand =
    { Path: string
      Line: int
      Column: int
      Executable: string
      Arguments: ParsedArgument list
      RawSource: string
      NormalizedCommand: string }

// ============================================================================
// Lexer state
// ============================================================================

/// State for the command lexer.
type LexerState =
    { Source: string
      Position: int
      Line: int
      Column: int }

// ============================================================================
// Command parsing
// ============================================================================

/// Parse command parts, handling quotes, escapes, and whitespace.
let rec parseCommandParts (input: string) : string list =
    let mutable result = []
    let mutable current = StringBuilder()
    let mutable inSingleQuote = false
    let mutable inDoubleQuote = false
    let mutable i = 0

    while i < input.Length do
        let c = input.[i]

        if c = '\\' && i + 1 < input.Length then
            // Escape character
            if not inSingleQuote then
                current.Append(input.[i + 1]) |> ignore
                i <- i + 1
            else
                current.Append(c) |> ignore
        elif c = '\'' && not inDoubleQuote then
            inSingleQuote <- not inSingleQuote
        elif c = '"' && not inSingleQuote then
            inDoubleQuote <- not inDoubleQuote
        elif Char.IsWhiteSpace(c) && not inSingleQuote && not inDoubleQuote then
            if current.Length > 0 then
                result <- current.ToString() :: result
                current.Clear() |> ignore
        else
            current.Append(c) |> ignore

        i <- i + 1

    if current.Length > 0 then
        result <- current.ToString() :: result

    List.rev result

/// Parse a single command line into a ParsedCommand.
and parseCommand (source: string) (location: SourceLocation) (filePath: string) : ParsedCommand option =
    let parts = parseCommandParts source

    match parts with
    | [] -> None
    | exe :: args ->
        let parsedArgs =
            args
            |> List.mapi (fun i arg ->
                { Index = i
                  Value = arg
                  IsQuoted = arg.StartsWith("\"") || arg.StartsWith("'")
                  IsVariable = Regex.IsMatch(arg, @"^\$[{@*]|\$\{") })

        let normalized =
            exe :: args
            |> List.map (fun a -> a.Replace("\"", "").Replace("'", ""))
            |> String.concat " "

        Some
            { ParsedCommand.Path = filePath
              ParsedCommand.Line = location.Line
              ParsedCommand.Column = location.Column
              ParsedCommand.Executable = exe
              ParsedCommand.Arguments = parsedArgs
              ParsedCommand.RawSource = source
              ParsedCommand.NormalizedCommand = normalized }

// ============================================================================
// Content extraction per parser kind
// ============================================================================

/// Normalize whitespace and join continuations for shell/Make content.
let normalizeShellContent (content: string) : string =
    let sb = StringBuilder()
    let lines = content.Split([| '\n'; '\r' |])

    for line in lines do
        let trimmed = line.TrimEnd([| ' '; '\t' |])

        if trimmed.EndsWith("\\") then
            // Line continuation - remove backslash and trailing space
            let withoutBackslash = trimmed.TrimEnd('\\').TrimEnd([| ' '; '\t' |])
            sb.Append(withoutBackslash) |> ignore
        else
            sb.Append(line) |> ignore
            sb.Append('\n') |> ignore

    sb.ToString()

/// Extract YAML run: block content from a workflow or action file.
let extractYamlRunBlocks (content: string) : (string * int) list =
    let results = ResizeArray<string * int>()
    let lines = content.Split([| '\n'; '\r' |])
    let mutable inBlock = false
    let mutable blockLines = []
    let mutable blockStartLine = 0

    for lineIdx in 0 .. lines.Length - 1 do
        let line = lines.[lineIdx]

        // Check for run: key (with possible | or > for blocks)
        if Regex.IsMatch(line, @"^\s*run:\s*[|>~]?\s*$") then
            inBlock <- true
            blockLines <- []
            blockStartLine <- lineIdx + 1 // 1-based
        elif inBlock then
            // Check if we've exited the block (non-indented non-empty line)
            if not (String.IsNullOrWhiteSpace line) then
                let leadingSpaces = line.Length - line.TrimStart().Length

                if leadingSpaces <= 1 && not (Regex.IsMatch(line, @"^\s+")) then
                    // Exited block
                    results.Add((String.concat "\n" blockLines, blockStartLine))
                    inBlock <- false
                    blockLines <- []
                else
                    blockLines <- line.TrimStart() :: blockLines
            elif not (Regex.IsMatch(line, @"^\s+$")) then
                blockLines <- line.TrimStart() :: blockLines

        // Single-line run: value
        let singleLine = Regex.Match(line, @"^\s*run:\s*(.+)\s*$")

        if singleLine.Success then
            let value = singleLine.Groups.[1].Value.Trim()

            if
                not (String.IsNullOrEmpty value)
                && not (value.StartsWith("|") || value.StartsWith(">"))
            then
                results.Add((value, lineIdx + 1)) |> ignore

    // Don't forget last block
    if inBlock && not (List.isEmpty blockLines) then
        results.Add((String.concat "\n" blockLines, blockStartLine))

    List.ofArray (results.ToArray())

/// Extract commands from Makefile content.
let extractMakeCommands (content: string) : (string * int) list =
    let results = ResizeArray<string * int>()
    let lines = content.Split([| '\n'; '\r' |])
    let mutable recipeLines = []
    let mutable inRecipe = false
    let mutable recipeStartLine = 0

    for lineIdx in 0 .. lines.Length - 1 do
        let line = lines.[lineIdx]

        // Tab or space-indented recipe line
        if Regex.IsMatch(line, @"^\t|^\s{8}") then
            inRecipe <- true

            if recipeStartLine = 0 then
                recipeStartLine <- lineIdx + 1 // 1-based

            let trimmed = line.TrimStart([| '\t'; ' ' |])
            // Keep variable references but remove comments
            let cleaned = Regex.Replace(trimmed, @"\s*#.*$", "").Trim()

            if not (String.IsNullOrEmpty cleaned) then
                recipeLines <- cleaned :: recipeLines
        elif inRecipe then
            // End of recipe
            if not (List.isEmpty recipeLines) then
                // Reverse to get correct order, join with semicolons for compound commands
                let recipe = String.concat "; " (List.rev recipeLines)
                results.Add((recipe, recipeStartLine)) |> ignore

            recipeLines <- []
            inRecipe <- false
            recipeStartLine <- 0

    if not (List.isEmpty recipeLines) then
        let recipe = String.concat "; " (List.rev recipeLines)
        results.Add((recipe, recipeStartLine)) |> ignore

    List.ofArray (results.ToArray())

/// Extract commands from Dockerfile RUN instructions.
let extractDockerfileCommands (content: string) : (string * int) list =
    let results = ResizeArray<string * int>()
    let lines = content.Split([| '\n'; '\r' |])
    let mutable continuation = []
    let mutable continuationStartLine = 0

    for lineIdx in 0 .. lines.Length - 1 do
        let line = lines.[lineIdx]
        let trimmed = line.Trim()

        // Handle line continuations
        if trimmed.EndsWith("\\") then
            if continuationStartLine = 0 then
                continuationStartLine <- lineIdx + 1

            continuation <- (trimmed.TrimEnd('\\')) :: continuation
        else
            let fullLine =
                if not (List.isEmpty continuation) then
                    String.concat " " (List.rev (trimmed :: continuation))
                else
                    trimmed

            continuation <- []
            continuationStartLine <- 0

            // Extract RUN commands
            let runMatch = Regex.Match(fullLine, @"^\s*RUN\s+(.+)\s*$", RegexOptions.IgnoreCase)

            if runMatch.Success then
                results.Add((runMatch.Groups.[1].Value.Trim(), lineIdx + 1)) |> ignore

    // Handle trailing continuation
    if not (List.isEmpty continuation) then
        let last = String.concat " " (List.rev continuation)
        let runMatch = Regex.Match(last, @"^\s*RUN\s+(.+)\s*$", RegexOptions.IgnoreCase)

        if runMatch.Success then
            results.Add((runMatch.Groups.[1].Value.Trim(), continuationStartLine)) |> ignore

    List.ofArray (results.ToArray())

// ============================================================================
// Command extraction from content
// ============================================================================

/// Extract all commands from a file based on its parser kind.
let extractCommandsFromContent
    (parserKind: Types.ParserKind)
    (content: string)
    (filePath: string)
    : ParsedCommand list =

    let rawCommands =
        match parserKind with
        | Types.ParserKind.Shell -> [ normalizeShellContent content ] |> List.mapi (fun i cmd -> (cmd, i + 1))
        | Types.ParserKind.Make -> extractMakeCommands content
        | Types.ParserKind.YamlRun -> extractYamlRunBlocks content
        | Types.ParserKind.Dockerfile -> extractDockerfileCommands content
        | Types.ParserKind.PlaintextCommand ->
            [ normalizeShellContent content ] |> List.mapi (fun i cmd -> (cmd, i + 1))

    rawCommands
    |> List.filter (fun (cmd, _) -> not (String.IsNullOrWhiteSpace cmd))
    |> List.map (fun (rawSource, line) ->
        match
            parseCommand
                rawSource
                { Line = line
                  Column = 1
                  AbsoluteOffset = 0 }
                filePath
        with
        | Some cmd -> cmd
        | None ->
            // Create a minimal command for non-empty content
            { Path = filePath
              Line = line
              Column = 1
              Executable = ""
              Arguments = []
              RawSource = rawSource
              NormalizedCommand = rawSource.Trim() })

// ============================================================================
// Effective executable extraction
// ============================================================================

/// Get the effective executable after safe prefixes like 'env' or 'command'.
let getEffectiveExecutable (cmd: ParsedCommand) : string =
    let args = cmd.Arguments |> List.map (fun a -> a.Value)

    match cmd.Executable.ToLowerInvariant() with
    | "env" ->
        // env FOO=bar git push -> git
        args
        |> List.tryFind (fun a -> not (a.Contains("=")))
        |> Option.defaultValue (if args.IsEmpty then "" else args.Head)
    | "command" ->
        // command git push -> git
        args |> List.tryHead |> Option.defaultValue cmd.Executable
    | "sh"
    | "bash"
    | "dash"
    | "zsh" ->
        // Check if first arg is -c
        match args with
        | "-c" :: rest when not (List.isEmpty rest) ->
            // sh -c "git push --force" -> extract from quoted string
            let script = String.concat " " rest
            let parts = parseCommandParts script
            parts |> List.tryHead |> Option.defaultValue cmd.Executable
        | _ -> cmd.Executable
    | exe when Regex.IsMatch(exe, @"(/git|/git$)", RegexOptions.IgnoreCase) ->
        // Absolute path to git
        "git"
    | _ -> cmd.Executable

// ============================================================================
// Executable classification
// ============================================================================

/// Classify the effective executable.
type ExecutableKind =
    | Git
    | Gh
    | Curl
    | Unknown

let classifyExecutable (exe: string) : ExecutableKind =
    match exe.ToLowerInvariant() with
    | "git" -> Git
    | "gh" -> Gh
    | "curl" -> Curl
    | _ -> Unknown

// ============================================================================
// Quote normalization
// ============================================================================

/// Normalize a Git option by removing adjacent quotes.
/// e.g., "--for" + "ce" -> "--force"
let denormalizeGitOption (s: string) : string = s.Replace("\"", "").Replace("'", "")

/// Check if a string looks like it contains adjacent quoted fragments.
let hasAdjacentQuotes (s: string) : bool =
    Regex.IsMatch(s, @"['""][^'""]*['""]['""]|['""]['""]")

/// Parse adjacent quote fragments into a normalized option.
let parseAdjacentQuotes (s: string) : string option =
    if hasAdjacentQuotes s then
        let normalized = denormalizeGitOption s

        if normalized.StartsWith("--") || normalized.StartsWith("-") then
            Some normalized
        else
            None
    else
        None
