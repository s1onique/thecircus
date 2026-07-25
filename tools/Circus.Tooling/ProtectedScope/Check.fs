module Circus.Tooling.ProtectedScope.Check

// =============================================================================
// Protected-scope authority – pure check logic
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION01
//
// Pure functions that:
//
//   1. Parse the ACT-scope declaration JSON into a
//      ``RawScopeDeclaration``.
//   2. Categorize each changed path against the declaration's
//      ``globally_protected`` and ``act_owned`` lists.
//
// Path matching rules:
//
//   * A path matches a globally_protected entry that ends with ``/``
//     when the path starts with the entry's prefix.
//   * A path matches an act_owned entry that ends with ``/`` when
//     the path starts with the entry's prefix.
//   * A path matches an act_owned entry without a trailing ``/``
//     when the path equals the entry exactly.
//
// The check is deterministic and free of IO. The orchestrator (CLI)
// provides the changed-path list via ``git diff --name-only``.
// =============================================================================

open System
open System.IO
open System.Text.Json

open Circus.Tooling.ProtectedScope.Domain

// -----------------------------------------------------------------------------
// JSON parsing
// -----------------------------------------------------------------------------

let private getProperty (el: JsonElement) (name: string) : JsonElement option =
    let mutable found = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &found) then Some found else None

let private parseString (el: JsonElement) (name: string) : Result<string, ScopeParseError> =
    match getProperty el name with
    | Some found when found.ValueKind = JsonValueKind.String -> Ok (found.GetString())
    | Some _ -> Error (InvalidField (name, "not a string"))
    | None -> Error (MissingField name)

let private parseStringList (el: JsonElement) (name: string) : Result<string list, ScopeParseError> =
    match getProperty el name with
    | Some found when found.ValueKind = JsonValueKind.Array ->
        let items = ResizeArray<string>()
        for item in found.EnumerateArray() do
            if item.ValueKind = JsonValueKind.String then
                items.Add(item.GetString())
            else
                Error (InvalidField (name, "array element is not a string")) |> ignore
        Ok (items |> Seq.toList)
    | Some _ -> Error (InvalidField (name, "not an array"))
    | None -> Error (MissingField name)

let private parseBool (el: JsonElement) (name: string) (defaultValue: bool) : bool =
    match getProperty el name with
    | Some found when found.ValueKind = JsonValueKind.True -> true
    | Some found when found.ValueKind = JsonValueKind.False -> false
    | _ -> defaultValue

let private parseInt (el: JsonElement) (name: string) : Result<int, ScopeParseError> =
    match getProperty el name with
    | Some found when found.ValueKind = JsonValueKind.Number -> Ok (found.GetInt32())
    | Some _ -> Error (InvalidField (name, "not a number"))
    | None -> Error (MissingField name)

let parseDeclaration (raw: string) : Result<RawScopeDeclaration, ScopeParseError> =
    try
        let mutable opts = JsonDocumentOptions()
        opts.AllowTrailingCommas <- false
        opts.CommentHandling <- JsonCommentHandling.Disallow
        opts.MaxDepth <- 64
        use doc = JsonDocument.Parse(raw, opts)
        let root = doc.RootElement
        if root.ValueKind <> JsonValueKind.Object then
            Error (JsonParseFailed "root is not an object")
        else
            match parseInt root "schema_version" with
            | Error e -> Error e
            | Ok schemaVersion ->
                if schemaVersion <> 1 then
                    Error (UnsupportedSchemaVersion schemaVersion)
                else
                    match parseString root "act_id" with
                    | Error e -> Error e
                    | Ok actId ->
                        match parseString root "baseline_commit_oid" with
                        | Error e -> Error e
                        | Ok baseline ->
                            match parseStringList root "globally_protected" with
                            | Error e -> Error e
                            | Ok globally ->
                                match parseStringList root "act_owned" with
                                | Error e -> Error e
                                | Ok owned ->
                                    let rejectUndeclared = parseBool root "reject_undeclared_changes" true
                                    let noProduction = parseBool root "do_not_authorize_production_or_migration_paths" true
                                    Ok {
                                        SchemaVersion = schemaVersion
                                        ActId = actId
                                        BaselineCommitOid = baseline
                                        GloballyProtected = globally
                                        ActOwned = owned
                                        RejectUndeclaredChanges = rejectUndeclared
                                        DoNotAuthorizeProductionOrMigrationPaths = noProduction
                                    }
    with ex ->
        Error (JsonParseFailed ex.Message)

// -----------------------------------------------------------------------------
// Path matching
// -----------------------------------------------------------------------------

let private patternMatches (path: string) (pattern: string) : bool =
    if pattern.EndsWith("/") then
        path.StartsWith(pattern, StringComparison.Ordinal)
    else
        String.Equals(path, pattern, StringComparison.Ordinal)

let categorizePath (declaration: RawScopeDeclaration) (path: string) : PathCategory =
    let matchesAny prefix = List.exists (patternMatches path) prefix
    if matchesAny declaration.GloballyProtected then
        GloballyProtected path
    elif matchesAny declaration.ActOwned then
        ActOwned path
    else
        Undeclared path

let categorize
    (declaration: RawScopeDeclaration)
    (changedPaths: string list)
    : ScopeCheckOutcome =
    let mutable globallyProtected = []
    let mutable actOwned = []
    let mutable undeclared = []
    for path in changedPaths do
        match categorizePath declaration path with
        | GloballyProtected p -> globallyProtected <- p :: globallyProtected
        | ActOwned p -> actOwned <- p :: actOwned
        | Undeclared p -> undeclared <- p :: undeclared
    {
        DeclarationPath = ""
        ActId = declaration.ActId
        BaselineCommitOid = declaration.BaselineCommitOid
        GloballyProtectedChanges = List.sort (List.rev globallyProtected)
        ActOwnedChanges = List.sort (List.rev actOwned)
        UndeclaredChanges = List.sort (List.rev undeclared)
        Authorisations = true
    }

// -----------------------------------------------------------------------------
// File helpers
// -----------------------------------------------------------------------------

let readDeclaration (path: string) : Result<RawScopeDeclaration, ScopeParseError> =
    if not (File.Exists path) then
        Error (JsonParseFailed (sprintf "file not found: %s" path))
    else
        try
            let raw = File.ReadAllText path
            parseDeclaration raw
        with ex ->
            Error (JsonParseFailed ex.Message)
