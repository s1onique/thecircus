module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateUpstreamDuplicateMappingTests

// =============================================================================
// Rule Candidate Upstream Duplicate Mapping Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION05
//
// Six tests: two per upstream identity kind.  Each test exercises the
// `mapEpisodeEngineFailure` adapter directly to prove that:
//   1. a single duplicate identity maps to exactly one
//      `DuplicateInputIdentities(kind, [identity])`;
//   2. several duplicate identities of one kind map in ordinal order to a
//      single `DuplicateInputIdentities(kind, sortedIdentities)`.
//
// Output order across the four `InputIdentityKind` cases is documented by
// the upstream-to-rule-candidate mapping:
//   EpisodeIdentity < ChangeSetIdentity < TransitionIdentity
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine

let private lines12 = [ 1; 2 ]
let private lines123 = [ 1; 2; 3 ]

let private episodeDup (id: string) (lines: int list) : EpisodeDuplicateIdentity =
    { Kind = EpisodeInputIdentityKind.RepairEpisode
      Identity = id
      OccurrenceLines = lines }

let private changeSetDup (id: string) (lines: int list) : EpisodeDuplicateIdentity =
    { Kind = EpisodeInputIdentityKind.ChangeSet
      Identity = id
      OccurrenceLines = lines }

let private transitionDup (id: string) (lines: int list) : EpisodeDuplicateIdentity =
    { Kind = EpisodeInputIdentityKind.DiagnosticTransition
      Identity = id
      OccurrenceLines = lines }

let assertExactMappedDuplicate
    (expectedKind: InputIdentityKind)
    (expectedIdentities: string list)
    (errors: EngineError list)
    : unit =
    match errors with
    | [ DuplicateInputIdentities(actualKind, actualIdentities) ] ->
        Expect.equal actualKind expectedKind "identity kind"
        Expect.equal actualIdentities expectedIdentities "identity list"
    | actual ->
        failwithf "expected DuplicateInputIdentities(%A, %A), got actual=%A" expectedKind expectedIdentities actual

[<Tests>]
let upstreamDuplicateMappingTests =
    testList
        "FSharpDiagnostics.RuleCandidates.UpstreamDuplicateMapping"
        [ // ----- Repair episodes -----
          test "single episode duplicate maps to one exact rule-candidate error" {
              let id = "ep-single-map"
              let mapped =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.DuplicateInputIdentities([ episodeDup id lines12 ])
                  )
              assertExactMappedDuplicate EpisodeIdentity [ id ] mapped
          }

          test "several episode duplicates map in ordinal order" {
              let mapped =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.DuplicateInputIdentities(
                          [ episodeDup "ep-zzz" lines12
                            episodeDup "ep-aaa" lines123
                            episodeDup "ep-mmm" lines12 ]
                      )
                  )
              assertExactMappedDuplicate EpisodeIdentity [ "ep-aaa"; "ep-mmm"; "ep-zzz" ] mapped
          }

          // ----- Change sets -----
          test "single change-set duplicate maps to one exact rule-candidate error" {
              let id = "cs-single-map"
              let mapped =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.DuplicateInputIdentities([ changeSetDup id lines12 ])
                  )
              assertExactMappedDuplicate ChangeSetIdentity [ id ] mapped
          }

          test "several change-set duplicates map in ordinal order" {
              let mapped =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.DuplicateInputIdentities(
                          [ changeSetDup "cs-zzz" lines12
                            changeSetDup "cs-aaa" lines123
                            changeSetDup "cs-mmm" lines12 ]
                      )
                  )
              assertExactMappedDuplicate ChangeSetIdentity [ "cs-aaa"; "cs-mmm"; "cs-zzz" ] mapped
          }

          // ----- Diagnostic transitions -----
          test "single transition duplicate maps to one exact rule-candidate error" {
              let id = "ep|fp-single-map"
              let mapped =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.DuplicateInputIdentities([ transitionDup id lines12 ])
                  )
              assertExactMappedDuplicate TransitionIdentity [ id ] mapped
          }

          test "several transition duplicates map in ordinal order" {
              let mapped =
                  mapEpisodeEngineFailure(
                      EpisodeEngineFailure.DuplicateInputIdentities(
                          [ transitionDup "zzz|1" lines12
                            transitionDup "aaa|1" lines123
                            transitionDup "mmm|1" lines12 ]
                      )
                  )
              assertExactMappedDuplicate TransitionIdentity [ "aaa|1"; "mmm|1"; "zzz|1" ] mapped
          } ]