module Circus.Tooling.ProtectedScope.Domain

// Protected-scope categorisation consumes the already parsed and Git-bound
// ScopeAuthority declaration.  There is intentionally no second declaration
// parser in this subsystem.

open Circus.Tooling.ScopeAuthority.Domain

type PathCategory =
    | GloballyProtected of path: string
    | ActOwned of path: string
    | Undeclared of path: string

type ScopeCheckOutcome = {
    EvaluatedCommitOid: string
    DeclarationPath: string
    DeclarationBlobOid: string
    PointerBlobOid: string
    ActId: string
    BaselineCommitOid: string
    GloballyProtectedChanges: string list
    ActOwnedChanges: string list
    UndeclaredChanges: string list
    Authorisations: bool
}

let isPathAuthorized outcome =
    outcome.Authorisations
    && List.isEmpty outcome.GloballyProtectedChanges
    && List.isEmpty outcome.UndeclaredChanges
