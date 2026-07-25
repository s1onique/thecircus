module Circus.Tooling.EvidenceValidator.Domain

// =============================================================================
// Evidence validator – pure domain
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION01
//
// Pure types and pure functions for validating per-ACT evidence files
// under ``factory/evidence/<act-id>/``. The validator enforces two
// contracts:
//
//   1. Non-recursive identity: the file's ``tested_subject_commit_oid``
//      MUST NOT equal the OID of the commit that contains the file.
//      Two values are equal in the strict hex form. Equality means the
//      evidence claim refers to its own containing commit and is
//      therefore self-referential.
//
//   2. Self-consistent payload hash: the ``evidence_payload_sha256``
//      field MUST equal the SHA-256 of the canonical JSON form where
//      the field is replaced by the documented placeholder
//      (64 ASCII zero characters). The canonical form is produced by
//      ``System.Text.Json`` with sorted keys, no whitespace, and
//      ASCII encoding.
//
// The module is pure: no IO, no subprocess, no environment. The
// process surface lives in the BoundedProcess adapter and is
// orchestrated by ``Validation.fs`` and ``Cli.fs``.
// =============================================================================

open System

// -----------------------------------------------------------------------------
// Result types
// -----------------------------------------------------------------------------

type Issue =
    | FileMissing of path: string
    | NotJsonObject of path: string
    | MissingField of path: string * field: string
    | FieldNotString of path: string * field: string
    | PayloadHashFieldMissing of path: string
    | PayloadHashFieldNotString of path: string
    | PlaceholderFieldMissing of path: string
    | PlaceholderFieldNotString of path: string
    | PlaceholderWrongWidth of path: string * actual: int
    | PayloadHashMismatch of path: string * expected: string * actual: string
    | SelfReferentialIdentity of path: string
        * field: string
        * claimed: string
        * containing_commit: string
    | ManifestLineCountWrong of path: string * expected: int64 * actual: int64
    | ManifestHashMismatch of path: string * filename: string * expected: string * actual: string

type Outcome = {
    Path: string
    Issues: Issue list
}

let outcomeOk (path: string) : Outcome = { Path = path; Issues = [] }

let outcomeFailed (path: string) (issues: Issue list) : Outcome =
    { Path = path; Issues = issues }

let isValid (o: Outcome) : bool = List.isEmpty o.Issues

// -----------------------------------------------------------------------------
// Constants
// -----------------------------------------------------------------------------

[<Literal>]
let Sha256Placeholder : string = "0000000000000000000000000000000000000000000000000000000000000000"

[<Literal>]
let Sha256HexWidth : int = 64

// -----------------------------------------------------------------------------
// Filing helpers
// -----------------------------------------------------------------------------

let private allIssues (outcomes: Outcome list) : Issue list =
    outcomes |> List.collect (fun o -> o.Issues)

let consolidate (outcomes: Outcome list) : Issue list = allIssues outcomes
