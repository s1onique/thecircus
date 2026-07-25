module Circus.Tooling.ProtectedScope.Domain

// =============================================================================
// Protected-scope authority – pure domain
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION01
//
// Pure types for the ACT-scope declaration and the protected-scope
// check outcome. The declaration is a small JSON document committed
// under ``docs/acts/<ACT-ID>.scope.json`` that names:
//
//   * the ACT;
//   * the baseline commit OID;
//   * the lists of globally protected paths (mandatory, never
//     touched) and ACT-owned paths (explicit authorisations for
//     this ACT).
//
// The check outcome records what happened during the evaluation so
// the CLI can produce a structured report.
// =============================================================================

open System

// -----------------------------------------------------------------------------
// Raw declaration (parsed from JSON)
// -----------------------------------------------------------------------------

type RawScopeDeclaration = {
    SchemaVersion: int
    ActId: string
    BaselineCommitOid: string
    GloballyProtected: string list
    ActOwned: string list
    RejectUndeclaredChanges: bool
    DoNotAuthorizeProductionOrMigrationPaths: bool
}

type ScopeParseError =
    | JsonParseFailed of message: string
    | MissingField of name: string
    | InvalidField of name: string * detail: string
    | UnsupportedSchemaVersion of actual: int

let parseErrorToString (e: ScopeParseError) : string =
    match e with
    | JsonParseFailed msg -> sprintf "json parse failed: %s" msg
    | MissingField name -> sprintf "missing required field: %s" name
    | InvalidField (name, detail) -> sprintf "invalid field %s: %s" name detail
    | UnsupportedSchemaVersion actual -> sprintf "unsupported schema_version: %d" actual

// -----------------------------------------------------------------------------
// Categorisation outcomes
// -----------------------------------------------------------------------------

type PathCategory =
    | GloballyProtected of path: string
    | ActOwned of path: string
    | Undeclared of path: string

type ScopeCheckOutcome = {
    DeclarationPath: string
    ActId: string
    BaselineCommitOid: string
    GloballyProtectedChanges: string list
    ActOwnedChanges: string list
    UndeclaredChanges: string list
    Authorisations: bool
}

let isPathAuthorized (outcome: ScopeCheckOutcome) : bool =
    outcome.Authorisations
       && List.isEmpty outcome.GloballyProtectedChanges
       && List.isEmpty outcome.UndeclaredChanges
