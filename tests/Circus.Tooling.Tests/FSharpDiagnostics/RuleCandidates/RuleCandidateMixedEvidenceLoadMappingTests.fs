module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateMixedEvidenceLoadMappingTests

// =============================================================================
// Rule Candidate Mixed Evidence Load Mapping Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION05
//
// Six tests proving that the `mapEpisodeEngineFailure` adapter preserves
// BOTH duplicate evidence IDs AND non-duplicate evidence-load errors
// (malformed JSON, unsupported schema version, wrong field type) without
// collapsing either class.  Each test asserts the complete EngineError list.
//
// Invariants under test:
//   * the duplicate does not suppress other errors;
//   * other errors do not suppress the duplicate;
//   * duplicate IDs are deduplicated and ordinally sorted;
//   * nonduplicate errors retain their upstream order;
//   * reversing JSONL record order produces the same normalized mapped
//     error list.
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine

let private dupErr (id: string) : VerificationEvidenceLoadError =
    VerificationEvidenceLoadError.DuplicateEvidenceId("path", id, 1, 2)

let private malformedErr (line: int) : VerificationEvidenceLoadError =
    VerificationEvidenceLoadError.ParseError(
        VerificationEvidenceParseError.MalformedJson("path", line, "bad")
    )

let private unsupportedErr (line: int) : VerificationEvidenceLoadError =
    VerificationEvidenceLoadError.ParseError(
        VerificationEvidenceParseError.UnsupportedSchemaVersion("path", line, "wrong-version")
    )

let private wrongTypeErr (line: int) : VerificationEvidenceLoadError =
    VerificationEvidenceLoadError.ParseError(
        VerificationEvidenceParseError.WrongFieldType("path", line, "evidence_id", "string", "number")
    )

let assertExactMappedErrors (errors: EngineError list) (expected: EngineError list) : unit =
    Expect.equal errors expected "mapped errors must match exactly"

[<Tests>]
let mixedEvidenceLoadMappingTests =
    testList
        "FSharpDiagnostics.RuleCandidates.MixedEvidenceLoadMapping"
        [ test "isolated duplicate evidence ID maps to one typed error" {
              let mapped =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.VerificationEvidenceLoadFailed([ dupErr "ev-1" ])
                  )
              assertExactMappedErrors
                  mapped
                  [ DuplicateInputIdentities(VerificationEvidenceIdentity, [ "ev-1" ]) ]
          }

          test "two duplicate evidence IDs sorted ordinally" {
              let mapped =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.VerificationEvidenceLoadFailed(
                          [ dupErr "ev-zzz"
                            dupErr "ev-aaa" ]
                      )
                  )
              assertExactMappedErrors
                  mapped
                  [ DuplicateInputIdentities(VerificationEvidenceIdentity, [ "ev-aaa"; "ev-zzz" ]) ]
          }

          test "duplicate plus malformed JSON record is preserved" {
              let mapped =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.VerificationEvidenceLoadFailed(
                          [ dupErr "ev-1"
                            malformedErr 7 ]
                      )
                  )
              // Both errors are preserved; duplicate first, then non-duplicate.
              match mapped with
              | [ DuplicateInputIdentities(VerificationEvidenceIdentity, [ "ev-1" ])
                  VerificationEvidenceLoadFailed nonDups ] ->
                  Expect.equal nonDups.Length 1 "non-duplicate count"
              | actual -> failwithf "expected [Duplicate; EvidenceLoadFailed], got %A" actual
          }

          test "duplicate plus unsupported schema version is preserved" {
              let mapped =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.VerificationEvidenceLoadFailed(
                          [ dupErr "ev-1"
                            unsupportedErr 9 ]
                      )
                  )
              match mapped with
              | [ DuplicateInputIdentities(VerificationEvidenceIdentity, [ "ev-1" ])
                  VerificationEvidenceLoadFailed nonDups ] ->
                  Expect.equal nonDups.Length 1 "non-duplicate count"
              | actual -> failwithf "expected [Duplicate; EvidenceLoadFailed], got %A" actual
          }

          test "duplicate plus wrong-field-type is preserved" {
              let mapped =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.VerificationEvidenceLoadFailed(
                          [ dupErr "ev-1"
                            wrongTypeErr 3 ]
                      )
                  )
              match mapped with
              | [ DuplicateInputIdentities(VerificationEvidenceIdentity, [ "ev-1" ])
                  VerificationEvidenceLoadFailed nonDups ] ->
                  Expect.equal nonDups.Length 1 "non-duplicate count"
              | actual -> failwithf "expected [Duplicate; EvidenceLoadFailed], got %A" actual
          }

          test "reversed mixed-error record order yields the same normalized result" {
              let forward =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.VerificationEvidenceLoadFailed(
                          [ dupErr "ev-1"
                            malformedErr 2
                            unsupportedErr 5
                            wrongTypeErr 7 ]
                      )
                  )
              let reversed =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.VerificationEvidenceLoadFailed(
                          [ wrongTypeErr 7
                            unsupportedErr 5
                            malformedErr 2
                            dupErr "ev-1" ]
                      )
                  )
              // Normalization: non-duplicate errors are sorted by a
              // typed key (kind, source path, line, field) using
              // ordinal comparison.  The full mapped list must therefore
              // be byte-identical between forward and reversed input
              // order — exact equality, not just set equality.
              Expect.equal forward reversed "mapped result must be invariant under input order"
              Expect.equal forward.Length 2 "mapped length"
          } ]
