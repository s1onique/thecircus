module Circus.Tooling.ScopeAuthority.Domain

// =============================================================================
// Active ACT scope authority -- strict, pure domain
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION03
//
// This is the one parser and validation authority for both the tracked active
// pointer and its scope declaration.  Parsing is deliberately stricter than
// System.Text.Json's default object model: duplicate properties, unknown
// properties, wrong types, non-ASCII hexadecimal OIDs, non-normalised paths,
// duplicate/overlapping ownership, unqualified prefixes, and ownership of
// repository-protected production or migration roots all fail closed.
// =============================================================================

open System
open System.Collections.Generic
open System.IO
open System.Text.Json

[<Literal>]
let ActiveScopePointerPath = ".factory/active-scope.json"

[<Literal>]
let SupportedSchemaVersion = 1

[<Literal>]
let Sha1Width = 40

[<Literal>]
let Sha256Width = 64

/// These roots are repository policy, not declaration-controlled data.  A
/// declaration can (and should) list them as globally protected, but can never
/// move either root into ACT-owned scope.
let RepositoryProtectedProductionAndMigrationRoots =
    [ "src/Circus.Persistence.Postgres/"; "db/migrations/" ]

type ActiveScopePointer = {
    SchemaVersion: int
    ActId: string
    DeclarationPath: string
    DeclarationBlobOid: string
    BaselineCommitOid: string
}

type PrefixQualification = {
    Path: string
    Reason: string
    ExpectedDescendants: string list
    SiblingMutationTest: string
}

type ScopeDeclaration = {
    SchemaVersion: int
    ActId: string
    ActClassification: string
    BaselineCommitOid: string
    Purpose: string
    GloballyProtected: string list
    ActOwned: string list
    PrefixQualifications: PrefixQualification list
    RejectUndeclaredChanges: bool
    DoNotAuthorizeProductionOrMigrationPaths: bool
}

type ScopeBinding = {
    EvaluatedCommitOid: string
    EvaluatedTreeOid: string
    PointerPath: string
    PointerBlobOid: string
    DeclarationPath: string
    DeclarationBlobOid: string
    BaselineCommitOid: string
    ActId: string
    Pointer: ActiveScopePointer
    Declaration: ScopeDeclaration
}

type ScopeAuthorityError =
    | JsonParseFailed of context: string * detail: string
    | JsonRootNotObject of context: string
    | DuplicateJsonProperty of context: string * property: string
    | MissingJsonProperty of context: string * property: string
    | UnknownJsonProperty of context: string * property: string
    | WrongJsonType of context: string * property: string * expected: string
    | UnsupportedSchemaVersion of context: string * actual: int
    | EmptyStringField of context: string * property: string
    | InvalidOid of context: string * property: string * value: string
    | InvalidRepositoryPath of context: string * property: string * value: string * detail: string
    | DuplicatePath of category: string * path: string
    | ScopeOverlap of globallyProtected: string * actOwned: string
    | MissingPrefixQualification of path: string
    | DuplicatePrefixQualification of path: string
    | OrphanPrefixQualification of path: string
    | InvalidPrefixQualification of path: string * detail: string
    | ProtectedRootOwned of ownedPath: string * protectedRoot: string
    | MissingRepositoryProtectedRoot of path: string
    | MandatoryBooleanFalse of property: string
    | GitOperationFailed of operation: string * detail: string
    | GitObjectMissing of objectKind: string * value: string
    | GitObjectIdentityMismatch of objectKind: string * expected: string * actual: string
    | GitObjectNotAncestor of ancestor: string * descendant: string
    | PointerDeclarationMismatch of field: string * pointerValue: string * declarationValue: string
    | CliPointerDisagreement of field: string * cliValue: string * pointerValue: string
    | InvalidUtf8Blob of path: string * detail: string

let errorToString error =
    match error with
    | JsonParseFailed (context, detail) -> sprintf "%s JSON parse failed: %s" context detail
    | JsonRootNotObject context -> sprintf "%s root must be a JSON object" context
    | DuplicateJsonProperty (context, property) -> sprintf "%s contains duplicate JSON property: %s" context property
    | MissingJsonProperty (context, property) -> sprintf "%s missing required property: %s" context property
    | UnknownJsonProperty (context, property) -> sprintf "%s contains unknown property: %s" context property
    | WrongJsonType (context, property, expected) -> sprintf "%s.%s must be %s" context property expected
    | UnsupportedSchemaVersion (context, actual) -> sprintf "%s.schema_version unsupported: %d" context actual
    | EmptyStringField (context, property) -> sprintf "%s.%s must be a non-empty string" context property
    | InvalidOid (context, property, value) -> sprintf "%s.%s is not a full 40/64-character ASCII hexadecimal OID: %s" context property value
    | InvalidRepositoryPath (context, property, value, detail) -> sprintf "%s.%s is not a normalized repository-relative POSIX path (%s): %s" context property detail value
    | DuplicatePath (category, path) -> sprintf "%s contains duplicate path: %s" category path
    | ScopeOverlap (globallyProtected, actOwned) -> sprintf "globally_protected path %s overlaps act_owned path %s" globallyProtected actOwned
    | MissingPrefixQualification path -> sprintf "directory prefix lacks qualification metadata: %s" path
    | DuplicatePrefixQualification path -> sprintf "directory prefix has duplicate qualification metadata: %s" path
    | OrphanPrefixQualification path -> sprintf "prefix qualification does not name a declared prefix: %s" path
    | InvalidPrefixQualification (path, detail) -> sprintf "prefix qualification invalid for %s: %s" path detail
    | ProtectedRootOwned (ownedPath, protectedRoot) -> sprintf "act_owned path %s overlaps repository-protected root %s" ownedPath protectedRoot
    | MissingRepositoryProtectedRoot path -> sprintf "globally_protected omits repository-protected root: %s" path
    | MandatoryBooleanFalse property -> sprintf "mandatory Boolean must be true: %s" property
    | GitOperationFailed (operation, detail) -> sprintf "Git operation %s failed: %s" operation detail
    | GitObjectMissing (objectKind, value) -> sprintf "Git %s does not exist: %s" objectKind value
    | GitObjectIdentityMismatch (objectKind, expected, actual) -> sprintf "Git %s identity mismatch: expected=%s actual=%s" objectKind expected actual
    | GitObjectNotAncestor (ancestor, descendant) -> sprintf "baseline %s is not an ancestor of %s" ancestor descendant
    | PointerDeclarationMismatch (field, pointerValue, declarationValue) -> sprintf "pointer/declaration %s mismatch: pointer=%s declaration=%s" field pointerValue declarationValue
    | CliPointerDisagreement (field, cliValue, pointerValue) -> sprintf "CLI/pointer disagreement for %s: cli=%s pointer=%s" field cliValue pointerValue
    | InvalidUtf8Blob (path, detail) -> sprintf "Git blob is not strict UTF-8 (%s): %s" path detail

let isAsciiHexOid (value: string) =
    if isNull value || (value.Length <> Sha1Width && value.Length <> Sha256Width) then
        false
    else
        value
        |> Seq.forall (fun c ->
            (c >= '0' && c <= '9')
            || (c >= 'a' && c <= 'f')
            || (c >= 'A' && c <= 'F'))

let private hasWindowsDrivePrefix (value: string) =
    value.Length >= 2 && Char.IsAsciiLetter(value.[0]) && value.[1] = ':'

/// Validate the repository path spelling used by pointers, declarations, and
/// CLI inputs.  Directory-prefix patterns may retain one terminal slash; file
/// paths may not.  No platform-specific normalization is performed because the
/// committed spelling itself is authoritative.
let validateRepositoryPath (allowDirectoryPrefix: bool) (value: string) : Result<unit, string> =
    if String.IsNullOrWhiteSpace value then
        Error "empty path"
    elif value.IndexOf('\u0000') >= 0 then
        Error "NUL is forbidden"
    elif value.Contains('\\') then
        Error "backslashes are forbidden"
    elif value.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted value || hasWindowsDrivePrefix value then
        Error "absolute/rooted paths are forbidden"
    elif value.EndsWith("/", StringComparison.Ordinal) && not allowDirectoryPrefix then
        Error "a file path may not end in slash"
    else
        let body =
            if allowDirectoryPrefix && value.EndsWith("/", StringComparison.Ordinal) then
                value.Substring(0, value.Length - 1)
            else
                value

        if String.IsNullOrEmpty body then
            Error "empty path"
        else
            let segments = body.Split('/')

            if segments |> Array.exists (fun segment -> String.IsNullOrEmpty segment) then
                Error "empty path segment"
            elif segments |> Array.exists (fun segment -> segment = "." || segment = "..") then
                Error "dot segments are forbidden"
            else
                Ok()

let isDirectoryPrefix (path: string) =
    not (isNull path) && path.EndsWith("/", StringComparison.Ordinal)

let patternMatches (pattern: string) (path: string) =
    if isDirectoryPrefix pattern then
        path.StartsWith(pattern, StringComparison.Ordinal)
    else
        String.Equals(pattern, path, StringComparison.Ordinal)

let patternsOverlap (left: string) (right: string) =
    String.Equals(left, right, StringComparison.Ordinal)
    || (isDirectoryPrefix left && right.StartsWith(left, StringComparison.Ordinal))
    || (isDirectoryPrefix right && left.StartsWith(right, StringComparison.Ordinal))

// -----------------------------------------------------------------------------
// Strict JSON primitives
// -----------------------------------------------------------------------------

exception private ScopeJsonException of ScopeAuthorityError

let private raiseScope error =
    raise (ScopeJsonException error)

let rec private rejectDuplicateProperties (context: string) (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.Object ->
        let seen = HashSet<string>(StringComparer.Ordinal)

        for property in element.EnumerateObject() do
            if not (seen.Add property.Name) then
                raiseScope (DuplicateJsonProperty(context, property.Name))

            rejectDuplicateProperties (context + "." + property.Name) property.Value
    | JsonValueKind.Array ->
        let mutable index = 0

        for item in element.EnumerateArray() do
            rejectDuplicateProperties (sprintf "%s[%d]" context index) item
            index <- index + 1
    | _ -> ()

let private requireExactProperties context (allowed: Set<string>) (element: JsonElement) =
    for property in element.EnumerateObject() do
        if not (Set.contains property.Name allowed) then
            raiseScope (UnknownJsonProperty(context, property.Name))

let private tryProperty (element: JsonElement) (name: string) =
    let mutable found = Unchecked.defaultof<JsonElement>

    if element.TryGetProperty(name, &found) then
        Some found
    else
        None

let private requiredProperty context name element =
    match tryProperty element name with
    | Some value -> value
    | None -> raiseScope (MissingJsonProperty(context, name))

let private requiredString context name element =
    let value = requiredProperty context name element

    if value.ValueKind <> JsonValueKind.String then
        raiseScope (WrongJsonType(context, name, "a string"))

    let text = value.GetString()

    if String.IsNullOrWhiteSpace text then
        raiseScope (EmptyStringField(context, name))

    text

let private requiredInt context name element =
    let value = requiredProperty context name element
    let mutable parsed = 0

    if value.ValueKind <> JsonValueKind.Number || not (value.TryGetInt32(&parsed)) then
        raiseScope (WrongJsonType(context, name, "an integer"))

    parsed

let private requiredBool context name element =
    let value = requiredProperty context name element

    match value.ValueKind with
    | JsonValueKind.True -> true
    | JsonValueKind.False -> false
    | _ -> raiseScope (WrongJsonType(context, name, "a Boolean"))

let private requiredStringArray context name element =
    let value = requiredProperty context name element

    if value.ValueKind <> JsonValueKind.Array then
        raiseScope (WrongJsonType(context, name, "an array of strings"))

    [ for item in value.EnumerateArray() do
          if item.ValueKind <> JsonValueKind.String then
              raiseScope (WrongJsonType(context, name, "an array of strings"))

          let text = item.GetString()

          if String.IsNullOrWhiteSpace text then
              raiseScope (EmptyStringField(context, name + "[]"))

          yield text ]

let private parseDocument (context: string) (raw: string) (parser: JsonElement -> 'a) =
    try
        let mutable options = JsonDocumentOptions()
        options.AllowTrailingCommas <- false
        options.CommentHandling <- JsonCommentHandling.Disallow
        options.MaxDepth <- 64
        use document = JsonDocument.Parse(raw, options)
        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            raiseScope (JsonRootNotObject context)

        rejectDuplicateProperties context root
        Ok(parser root)
    with
    | ScopeJsonException error -> Error error
    | :? JsonException as error -> Error(JsonParseFailed(context, error.Message))
    | :? FormatException as error -> Error(JsonParseFailed(context, error.Message))
    | :? InvalidOperationException as error -> Error(JsonParseFailed(context, error.Message))

let private validateOid context property value =
    if isAsciiHexOid value then
        Ok()
    else
        Error(InvalidOid(context, property, value))

let private validatePath context property allowPrefix value =
    match validateRepositoryPath allowPrefix value with
    | Ok() -> Ok()
    | Error detail -> Error(InvalidRepositoryPath(context, property, value, detail))

let private firstError (results: Result<unit, ScopeAuthorityError> list) =
    results |> List.tryPick (function | Error error -> Some error | Ok() -> None)

let validatePointer (pointer: ActiveScopePointer) =
    let checks =
        [ if pointer.SchemaVersion <> SupportedSchemaVersion then
              Error(UnsupportedSchemaVersion("active-scope", pointer.SchemaVersion))
          else
              Ok()
          if String.IsNullOrWhiteSpace pointer.ActId then
              Error(EmptyStringField("active-scope", "act_id"))
          else
              Ok()
          validatePath "active-scope" "declaration_path" false pointer.DeclarationPath
          validateOid "active-scope" "declaration_blob_oid" pointer.DeclarationBlobOid
          validateOid "active-scope" "baseline_commit_oid" pointer.BaselineCommitOid ]

    match firstError checks with
    | Some error -> Error error
    | None -> Ok pointer

let parseActiveScopePointer raw =
    parseDocument "active-scope" raw (fun root ->
        requireExactProperties
            "active-scope"
            (Set.ofList
                [ "schema_version"
                  "act_id"
                  "declaration_path"
                  "declaration_blob_oid"
                  "baseline_commit_oid" ])
            root

        let pointer =
            { SchemaVersion = requiredInt "active-scope" "schema_version" root
              ActId = requiredString "active-scope" "act_id" root
              DeclarationPath = requiredString "active-scope" "declaration_path" root
              DeclarationBlobOid = requiredString "active-scope" "declaration_blob_oid" root
              BaselineCommitOid = requiredString "active-scope" "baseline_commit_oid" root }

        match validatePointer pointer with
        | Ok value -> value
        | Error error -> raiseScope error)

let private parsePrefixQualification (element: JsonElement) =
    let context = "scope-declaration.prefix_qualifications[]"

    if element.ValueKind <> JsonValueKind.Object then
        raiseScope (WrongJsonType("scope-declaration", "prefix_qualifications[]", "an object"))

    requireExactProperties
        context
        (Set.ofList [ "path"; "reason"; "expected_descendants"; "sibling_mutation_test" ])
        element

    { Path = requiredString context "path" element
      Reason = requiredString context "reason" element
      ExpectedDescendants = requiredStringArray context "expected_descendants" element
      SiblingMutationTest = requiredString context "sibling_mutation_test" element }

let private requiredQualifications element =
    let value = requiredProperty "scope-declaration" "prefix_qualifications" element

    if value.ValueKind <> JsonValueKind.Array then
        raiseScope (WrongJsonType("scope-declaration", "prefix_qualifications", "an array of objects"))

    [ for item in value.EnumerateArray() -> parsePrefixQualification item ]

let private duplicateIn category values =
    let seen = HashSet<string>(StringComparer.Ordinal)
    values
    |> List.tryPick (fun value -> if seen.Add value then None else Some(DuplicatePath(category, value)))

let validateDeclaration (declaration: ScopeDeclaration) =
    let mutable errors: ScopeAuthorityError list = []
    let add error = errors <- error :: errors

    if declaration.SchemaVersion <> SupportedSchemaVersion then
        add (UnsupportedSchemaVersion("scope-declaration", declaration.SchemaVersion))

    if String.IsNullOrWhiteSpace declaration.ActId then
        add (EmptyStringField("scope-declaration", "act_id"))

    if String.IsNullOrWhiteSpace declaration.ActClassification then
        add (EmptyStringField("scope-declaration", "act_classification"))

    if String.IsNullOrWhiteSpace declaration.Purpose then
        add (EmptyStringField("scope-declaration", "purpose"))

    match validateOid "scope-declaration" "baseline_commit_oid" declaration.BaselineCommitOid with
    | Error error -> add error
    | Ok() -> ()

    for category, paths in
        [ "globally_protected", declaration.GloballyProtected
          "act_owned", declaration.ActOwned ] do
        for path in paths do
            match validatePath "scope-declaration" category true path with
            | Error error -> add error
            | Ok() -> ()

        match duplicateIn category paths with
        | Some error -> add error
        | None -> ()

    for globalPath in declaration.GloballyProtected do
        for ownedPath in declaration.ActOwned do
            if patternsOverlap globalPath ownedPath then
                add (ScopeOverlap(globalPath, ownedPath))

    for protectedRoot in RepositoryProtectedProductionAndMigrationRoots do
        if not (declaration.GloballyProtected |> List.exists (fun path -> String.Equals(path, protectedRoot, StringComparison.Ordinal))) then
            add (MissingRepositoryProtectedRoot protectedRoot)

        for ownedPath in declaration.ActOwned do
            if patternsOverlap ownedPath protectedRoot then
                add (ProtectedRootOwned(ownedPath, protectedRoot))

    if not declaration.RejectUndeclaredChanges then
        add (MandatoryBooleanFalse "reject_undeclared_changes")

    if not declaration.DoNotAuthorizeProductionOrMigrationPaths then
        add (MandatoryBooleanFalse "do_not_authorize_production_or_migration_paths")

    // Only ActOwned directory prefixes require qualification metadata.
    // GloballyProtected prefixes restrict authority; they do not broaden it
    // and therefore do not need sibling-authorization justification.
    let declaredPrefixes =
        declaration.ActOwned
        |> List.filter isDirectoryPrefix

    let qualificationPaths = declaration.PrefixQualifications |> List.map (fun item -> item.Path)

    match duplicateIn "prefix_qualifications" qualificationPaths with
    | Some(DuplicatePath(_, path)) -> add (DuplicatePrefixQualification path)
    | _ -> ()

    for prefix in declaredPrefixes do
        let matches = declaration.PrefixQualifications |> List.filter (fun item -> item.Path = prefix)

        match matches with
        | [] -> add (MissingPrefixQualification prefix)
        | [ _ ] -> ()
        | _ -> add (DuplicatePrefixQualification prefix)

    for qualification in declaration.PrefixQualifications do
        if not (List.contains qualification.Path declaredPrefixes) then
            add (OrphanPrefixQualification qualification.Path)

        if not (isDirectoryPrefix qualification.Path) then
            add (InvalidPrefixQualification(qualification.Path, "path is not a directory prefix"))

        if String.IsNullOrWhiteSpace qualification.Reason then
            add (InvalidPrefixQualification(qualification.Path, "reason is empty"))

        if List.isEmpty qualification.ExpectedDescendants then
            add (InvalidPrefixQualification(qualification.Path, "expected_descendants is empty"))

        match duplicateIn "expected_descendants" qualification.ExpectedDescendants with
        | Some _ -> add (InvalidPrefixQualification(qualification.Path, "expected_descendants contains a duplicate"))
        | None -> ()

        for descendant in qualification.ExpectedDescendants do
            match validateRepositoryPath false descendant with
            | Error detail -> add (InvalidPrefixQualification(qualification.Path, "invalid expected descendant: " + detail))
            | Ok() ->
                if not (patternMatches qualification.Path descendant) then
                    add (InvalidPrefixQualification(qualification.Path, sprintf "expected descendant is outside prefix: %s" descendant))

        match validateRepositoryPath false qualification.SiblingMutationTest with
        | Error detail -> add (InvalidPrefixQualification(qualification.Path, "invalid sibling_mutation_test: " + detail))
        | Ok() ->
            if patternMatches qualification.Path qualification.SiblingMutationTest then
                add (InvalidPrefixQualification(qualification.Path, "sibling_mutation_test is inside the qualified prefix"))

            let authorizedByAny =
                declaration.GloballyProtected @ declaration.ActOwned
                |> List.exists (fun pattern -> patternMatches pattern qualification.SiblingMutationTest)

            if authorizedByAny then
                add (InvalidPrefixQualification(qualification.Path, "sibling_mutation_test is declared instead of undeclared"))

    match List.rev errors with
    | first :: _ -> Error first
    | [] -> Ok declaration

let parseScopeDeclaration raw =
    parseDocument "scope-declaration" raw (fun root ->
        requireExactProperties
            "scope-declaration"
            (Set.ofList
                [ "schema_version"
                  "act_id"
                  "act_classification"
                  "baseline_commit_oid"
                  "purpose"
                  "globally_protected"
                  "act_owned"
                  "prefix_qualifications"
                  "reject_undeclared_changes"
                  "do_not_authorize_production_or_migration_paths" ])
            root

        let declaration =
            { SchemaVersion = requiredInt "scope-declaration" "schema_version" root
              ActId = requiredString "scope-declaration" "act_id" root
              ActClassification = requiredString "scope-declaration" "act_classification" root
              BaselineCommitOid = requiredString "scope-declaration" "baseline_commit_oid" root
              Purpose = requiredString "scope-declaration" "purpose" root
              GloballyProtected = requiredStringArray "scope-declaration" "globally_protected" root
              ActOwned = requiredStringArray "scope-declaration" "act_owned" root
              PrefixQualifications = requiredQualifications root
              RejectUndeclaredChanges = requiredBool "scope-declaration" "reject_undeclared_changes" root
              DoNotAuthorizeProductionOrMigrationPaths =
                requiredBool "scope-declaration" "do_not_authorize_production_or_migration_paths" root }

        match validateDeclaration declaration with
        | Ok value -> value
        | Error error -> raiseScope error)
