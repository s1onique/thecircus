module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceAliasFixture

// =============================================================================
// Verification Evidence Alias Test Fixtures
//
// Shared fixtures and helpers for alias parser matrix tests.
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION03:
// Spec §7-§15 — explicit canonical-only/alias-only/both-present builders,
// raw-property builder, fixture self-verification helpers, and the
// deterministic evidence-ID framing (spec §13).
// =============================================================================

open System
open System.IO
open System.Security.Cryptography
open System.Text

open Circus.Tooling.FSharpDiagnostics.Manifest
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths
open Circus.Tooling.FSharpDiagnostics.Paths

// -----------------------------------------------------------------------------
// Test Data Constants
// -----------------------------------------------------------------------------

/// Valid 40-character commit OID
let validCommitOid = String.replicate 40 "a"

/// Valid 40-character tree OID
let validTreeOid = String.replicate 40 "a"

/// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01:
/// Spec §13 - deterministic evidence ID generation for test fixtures.
/// The framing is:
///   sha256( UTF8("verification-evidence-alias-fixture-v1") + NUL + UTF8(testCaseKey) )
/// This guarantees:
///   * output length is exactly 64 lowercase hexadecimal characters
///   * the same test_case_key always produces the same ID
///   * different test_case_key values produce different IDs
///   * the result is independent of any global counter, timestamp, GUID, or filesystem path
let evidenceId (testCaseKey: string) : string =
    let prefix = Encoding.UTF8.GetBytes "verification-evidence-alias-fixture-v1"
    let nul = [| byte 0 |]
    let keyBytes = Encoding.UTF8.GetBytes testCaseKey

    use h = SHA256.Create()
    let hash = h.ComputeHash(Array.concat [ prefix; nul; keyBytes ])

    hash
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""
    |> fun s -> if s.Length <> 64 then failwithf "deterministicEvidenceId produced %d chars, expected 64" s.Length else s

// -----------------------------------------------------------------------------
// Directory Helpers
// -----------------------------------------------------------------------------

let tempDir (label: string) =
    let dir = Path.Combine(Path.GetTempPath(), label + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let cleanup (dir: string) =
    try if Directory.Exists dir then Directory.Delete(dir, true) with _ -> ()

let createMinimalStructure (dir: string) =
    let declDir = Path.Combine(dir, canonicalRootRelative, "corpus", "episodes", "declarations")
    let capDir = Path.Combine(dir, canonicalRootRelative, "corpus", "captures")
    Directory.CreateDirectory declDir |> ignore
    Directory.CreateDirectory capDir |> ignore

// -----------------------------------------------------------------------------
// Evidence File Helpers
// -----------------------------------------------------------------------------

let writeEvidence (dir: string) (records: string list) =
    let path = Path.Combine(dir, verificationEvidenceCanonicalPath)
    let evidenceDir = Path.GetDirectoryName(path)
    if not (Directory.Exists evidenceDir) then Directory.CreateDirectory(evidenceDir) |> ignore
    File.WriteAllLines(path, records)

/// Run verification pipeline on a directory
let runVerify (dir: string) : VerificationResult =
    verifyPipeline dir defaultEngineOptions

// -----------------------------------------------------------------------------
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION03:
// Spec §7 — explicit fixture builders whose emitted JSON shape is precisely
// controlled.  Each builder guarantees the physical property list of the
// emitted record and is the only path by which a record is constructed for
// the matrix tests.
// -----------------------------------------------------------------------------

/// Map of the four canonical/alias pairs the parser recognises.
let private aliasPairs: (string * string) list =
    [ "kind", "verification_kind"
      "status", "verification_result"
      "command", "verification_command"
      "exit_code", "verification_exit_code" ]

/// True iff `name` is the canonical member of one of the recognised pairs.
let private isCanonicalField (name: string) : bool =
    aliasPairs |> List.exists (fun (canon, _) -> canon = name)

/// True iff `name` is the alias member of one of the recognised pairs.
let private isAliasField (name: string) : bool =
    aliasPairs |> List.exists (fun (_, alias) -> alias = name)

/// True iff `name` is one half of a recognised pair (canonical or alias).
let private isPairField (name: string) : bool =
    isCanonicalField name || isAliasField name

/// Compose a JSON object string from a list of (key, raw-JSON-value) pairs.
/// Preserves order, allows duplicate keys, and uses the supplied literal
/// JSON values verbatim.  Caller is responsible for valid JSON.
let private composeObject (properties: (string * string) list) : string =
    let body =
        properties
        |> List.map (fun (k, v) -> sprintf "\"%s\":%s" k v)
        |> String.concat ","
    "{" + body + "}"

/// Bare metadata present in every valid evidence record.  These fields are
/// derived from the test case key and are not part of the semantic-field
/// pair contract under test.
let private baseMetadata (evId: string) (epId: string) : (string * string) list =
    [ "schema_version", "\"verification-evidence-v1\""
      "evidence_id", sprintf "\"%s\"" evId
      "episode_id", sprintf "\"%s\"" epId ]

/// Canonical-only defaults for the four semantic fields and the two
/// commit/tree OID fields.  Used by the canonical-only, alias-only, and
/// both-present builders; the raw-properties builder does NOT include
/// these so the caller can fully control property names and order.
let private semanticDefaults : (string * string) list =
    [ "kind", "\"focused_test\""
      "command", "\"dotnet test\""
      "status", "\"pass\""
      "exit_code", "0"
      "tested_commit_oid", sprintf "\"%s\"" validCommitOid
      "tested_tree_oid", sprintf "\"%s\"" validTreeOid ]

/// Required fields present in every valid evidence record for the
/// canonical-only, alias-only, and both-present builders.
let private otherRequiredFields (evId: string) (epId: string) : (string * string) list =
    baseMetadata evId epId @ semanticDefaults

// -----------------------------------------------------------------------------
// Spec §7.1 — canonical-only builder
// -----------------------------------------------------------------------------

/// Map of canonical → alias for the four recognised pairs.
let private aliasOf: Map<string, string> =
    Map.ofList
        [ "kind", "verification_kind"
          "status", "verification_result"
          "command", "verification_command"
          "exit_code", "verification_exit_code" ]

/// Strip any default occurrence of `fieldName` AND, when `fieldName` is an
/// alias, the corresponding canonical field.  Guarantees the supplied test
/// value is the sole occurrence of the field pair under test.
let private replaceDefault
    (defaults: (string * string) list)
    (fieldName: string)
    (newValue: string)
    : (string * string) list =
    let canonicalForAlias =
        aliasOf
        |> Map.tryFindKey (fun _ v -> v = fieldName)
        |> Option.defaultValue fieldName
    defaults
    |> List.filter (fun (k, _) -> k <> fieldName && k <> canonicalForAlias)
    |> List.append [ fieldName, newValue ]

/// Emit a verification-evidence JSON record in which ONLY the canonical
/// spelling of the tested field is present.  The alias spelling is absent.
/// All other required fields are valid and canonical-only.
let verificationEvidenceCanonicalOnly
    (testCaseKey: string)
    (canonicalFieldName: string)
    (canonicalJsonValue: string)
    : string =
    let evId = evidenceId testCaseKey
    let epId = "ep-" + testCaseKey
    let defaults = otherRequiredFields evId epId
    let properties = replaceDefault defaults canonicalFieldName canonicalJsonValue
    composeObject properties

// -----------------------------------------------------------------------------
// Spec §7.2 — alias-only builder
// -----------------------------------------------------------------------------

/// Emit a verification-evidence JSON record in which ONLY the alias
/// spelling of the tested field is present.  The canonical spelling is
/// absent.  All other required fields are valid and canonical-only.
let verificationEvidenceAliasOnly
    (testCaseKey: string)
    (aliasFieldName: string)
    (aliasJsonValue: string)
    : string =
    let evId = evidenceId testCaseKey
    let epId = "ep-" + testCaseKey
    let defaults = otherRequiredFields evId epId
    let properties = replaceDefault defaults aliasFieldName aliasJsonValue
    composeObject properties

// -----------------------------------------------------------------------------
// Spec §7.3 — both-present builder
// -----------------------------------------------------------------------------

/// Emit a verification-evidence JSON record in which BOTH the canonical
/// spelling AND the alias spelling of the tested field are present exactly
/// once.  All other required fields are valid and canonical-only.
let verificationEvidenceBothPresent
    (testCaseKey: string)
    (canonicalFieldName: string)
    (canonicalJsonValue: string)
    (aliasFieldName: string)
    (aliasJsonValue: string)
    : string =
    let evId = evidenceId testCaseKey
    let epId = "ep-" + testCaseKey
    let defaults = otherRequiredFields evId epId
    let properties =
        defaults
        |> List.filter (fun (k, _) -> k <> canonicalFieldName && k <> aliasFieldName)
        |> List.append
            [ canonicalFieldName, canonicalJsonValue
              aliasFieldName, aliasJsonValue ]
    composeObject properties

// -----------------------------------------------------------------------------
// Spec §7.4 — raw JSON builder (preserves order, duplicates, raw values)
// -----------------------------------------------------------------------------

/// Emit a verification-evidence JSON record with arbitrary raw
/// (key, JSON-value) pairs.  Preserves property order, supports repeated
/// names, and uses the supplied literal JSON values verbatim.  Required
/// fields are still present and canonical-only; the caller may supply any
/// number of additional properties — including duplicates and properties
/// not in the recognised schema.
let verificationEvidenceRawProperties
    (testCaseKey: string)
    (semanticProperties: (string * string) list)
    : string =
    let evId = evidenceId testCaseKey
    let epId = "ep-" + testCaseKey
    // The raw builder only injects bare metadata.  The caller is
    // responsible for supplying every other property (including duplicates,
    // case-variants, and any required semantic fields) so that the
    // emitted shape is fully determined by the caller's list.
    let properties = baseMetadata evId epId @ semanticProperties
    composeObject properties

// -----------------------------------------------------------------------------
// Spec §8 — fixture self-verification helpers
// -----------------------------------------------------------------------------

/// Count occurrences of `propertyName` as a top-level property of the
/// supplied JSON record.  Order-preserving (i.e. counts duplicates).
let propertyOccurrences (json: string) (propertyName: string) : int =
    match parseJson json with
    | JsonObject fields ->
        fields
        |> List.filter (fun (k, _) -> k = propertyName)
        |> List.length
    | _ -> -1

/// Return the list of top-level property names in the supplied JSON
/// record, in emission order.
let propertyNames (json: string) : string list =
    match parseJson json with
    | JsonObject fields -> fields |> List.map fst
    | _ -> []

/// True iff `name` is one half of a recognised canonical/alias pair.
let isAliasOrCanonicalField (name: string) : bool =
    isPairField name

/// True iff `name` is the canonical half of a recognised pair.
let isCanonicalFieldPublic (name: string) : bool =
    isCanonicalField name

/// True iff `name` is the alias half of a recognised pair.
let isAliasFieldPublic (name: string) : bool =
    isAliasField name
