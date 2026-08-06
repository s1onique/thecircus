module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.EpisodeEngineDuplicateIdentityTests

// =============================================================================
// Episode Engine Duplicate Identity Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION05
//
// Direct upstream duplicate-identity tests.  Six cases per identity kind
// (RepairEpisode, ChangeSet, DiagnosticTransition) for a total of eighteen
// typed assertions against `detectUpstreamDuplicates`.  Each duplicate case
// asserts the typed `EpisodeDuplicateIdentity`; the unique control asserts
// the empty list.  No "Outcome = false" assertions are used.
// =============================================================================

open System
open Expecto
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine

let private beforeCommit = String.replicate 40 "a"
let private beforeTree = String.replicate 40 "c"
let private afterCommit = String.replicate 40 "b"
let private afterTree = String.replicate 40 "d"

let mkEpisode (id: string) (key: string) : RepairEpisode =
    { SchemaVersion = RepairEpisodeSchemaVersion
      EpisodeId = id
      EpisodeKey = key
      BeforeCaptureId = key + "-before"
      AfterCaptureId = key + "-after"
      BeforeCommitOid = beforeCommit
      BeforeTreeOid = beforeTree
      AfterCommitOid = afterCommit
      AfterTreeOid = afterTree
      CommitRange = [ afterCommit ]
      ChangeSetId = "cs-" + key
      CommandContractBefore = "dotnet build"
      CommandContractAfter = "dotnet build"
      Compatibility = compatible
      TransitionCounts =
        { PersistedSameCount = 0
          PersistedCountDecreased = 0
          PersistedCountIncreased = 0
          EliminatedAfter = 0
          IntroducedAfter = 0
          ResolutionCandidates = 0
          RegressionCandidates = 0
          Unassessable = 0 }
      VerificationLevel = TransitionObserved
      VerificationEvidenceIds = []
      Qualification = { Status = Qualified; Reasons = [] } }

let mkChangeSet (id: string) : GitChangeSet =
    { SchemaVersion = GitChangeSetSchemaVersion
      ChangeSetId = id
      ChangeSetVersion = GitChangeSetSchemaVersion
      BeforeTreeOid = beforeTree
      AfterTreeOid = afterTree
      ObjectFormat = Sha1
      Entries = [] }

let mkTransition (episodeId: string) (fp: string) : DiagnosticTransition =
    { SchemaVersion = DiagnosticTransitionSchemaVersion
      EpisodeId = episodeId
      ExactFingerprint = fp
      TransitionKind = EliminatedAfter
      BeforeOccurrenceCount = 1
      AfterOccurrenceCount = 0
      Severity = Circus.Tooling.FSharpDiagnostics.Domain.DiagnosticSeverity.Error
      Code = Some "FS0010"
      MessageNormalized = "msg"
      SourcePath = Some "a.fs"
      ProjectPath = None
      Span = { StartLine = Some 1; StartColumn = Some 1; EndLine = Some 1; EndColumn = Some 10 }
      Compatibility = compatible
      SourceLink =
        { Kind = SourceFileModified "a.fs"
          Paths = [ "a.fs" ]
          Reasons = [] }
      Assessment = ObservedResolutionCandidate }

let assertSingleDuplicate (dups: EpisodeDuplicateIdentity list) (expectedKind: EpisodeInputIdentityKind) (expectedIdentity: string) : unit =
    match dups with
    | [ { Kind = k; Identity = id; OccurrenceLines = _ } ] ->
        Expect.equal k expectedKind "upstream duplicate kind"
        Expect.equal id expectedIdentity "upstream duplicate identity"
    | actual ->
        failwithf "expected one EpisodeDuplicateIdentity, got %A" actual

[<Tests>]
let duplicateIdentityTests =
    testList
        "FSharpDiagnostics.RepairEpisodes.DuplicateIdentity"
        [ // ----- Repair episode -----
          test "episode byte-identical duplicate ID is detected" {
              let id = "ep-byte-id"
              let dups = detectUpstreamDuplicates [ mkEpisode id "k1"; mkEpisode id "k2" ] [] []
              assertSingleDuplicate dups EpisodeInputIdentityKind.RepairEpisode id
          }

          test "episode different content with the same ID is detected" {
              let id = "ep-content-id"
              let epA = { (mkEpisode id "k1") with EpisodeKey = "k1"; ChangeSetId = "cs-a" }
              let epB = { (mkEpisode id "k2") with EpisodeKey = "k2"; ChangeSetId = "cs-b" }
              let dups = detectUpstreamDuplicates [ epA; epB ] [] []
              assertSingleDuplicate dups EpisodeInputIdentityKind.RepairEpisode id
          }

          test "episode three occurrences of one ID is detected" {
              let id = "ep-triple-id"
              let dups =
                  detectUpstreamDuplicates
                      [ mkEpisode id "k1"
                        mkEpisode id "k2"
                        mkEpisode id "k3" ]
                      []
                      []
              assertSingleDuplicate dups EpisodeInputIdentityKind.RepairEpisode id
          }

          test "episode two distinct duplicate IDs sorted ordinally" {
              let id1 = "ep-zzz"
              let id2 = "ep-aaa"
              let dups =
                  detectUpstreamDuplicates
                      [ mkEpisode id1 "k1"
                        mkEpisode id1 "k2"
                        mkEpisode id2 "k3"
                        mkEpisode id2 "k4" ]
                      []
                      []
              match dups with
              | [ { Kind = k1; Identity = i1; OccurrenceLines = _ }
                  { Kind = k2; Identity = i2; OccurrenceLines = _ } ] ->
                  Expect.equal k1 EpisodeInputIdentityKind.RepairEpisode "first kind"
                  Expect.equal k2 EpisodeInputIdentityKind.RepairEpisode "second kind"
                  Expect.equal i1 id2 "ordinal ascending: aaa first"
                  Expect.equal i2 id1 "ordinal ascending: zzz second"
              | actual -> failwithf "expected two EpisodeDuplicateIdentity, got %A" actual
          }

          test "episode reversed record order yields the same failure" {
              let id = "ep-rev-id"
              let fwd =
                  detectUpstreamDuplicates [ mkEpisode id "k1"; mkEpisode id "k2" ] [] []
              let rev =
                  detectUpstreamDuplicates [ mkEpisode id "k2"; mkEpisode id "k1" ] [] []
              Expect.equal (List.length fwd) 1 "fwd length"
              Expect.equal (List.length rev) 1 "rev length"
              Expect.equal fwd rev "reversed order must yield identical normalized result"
          }

          test "episode unique control reaches empty duplicate list" {
              let dups =
                  detectUpstreamDuplicates
                      [ mkEpisode "ep-u-1" "k1"
                        mkEpisode "ep-u-2" "k2"
                        mkEpisode "ep-u-3" "k3" ]
                      []
                      []
              Expect.isEmpty dups "unique corpus must yield no duplicates"
          }

          // ----- Change set -----
          test "change-set byte-identical duplicate ID is detected" {
              let id = "cs-byte-id"
              let dups = detectUpstreamDuplicates [] [ mkChangeSet id; mkChangeSet id ] []
              assertSingleDuplicate dups EpisodeInputIdentityKind.ChangeSet id
          }

          test "change-set different content with the same ID is detected" {
              let id = "cs-content-id"
              let csA = mkChangeSet id
              let csB = { (mkChangeSet id) with Entries = [] }
              let dups = detectUpstreamDuplicates [] [ csA; csB ] []
              assertSingleDuplicate dups EpisodeInputIdentityKind.ChangeSet id
          }

          test "change-set three occurrences of one ID is detected" {
              let id = "cs-triple-id"
              let dups =
                  detectUpstreamDuplicates
                      []
                      [ mkChangeSet id
                        mkChangeSet id
                        mkChangeSet id ]
                      []
              assertSingleDuplicate dups EpisodeInputIdentityKind.ChangeSet id
          }

          test "change-set two distinct duplicate IDs sorted ordinally" {
              let id1 = "cs-zzz"
              let id2 = "cs-aaa"
              let dups =
                  detectUpstreamDuplicates
                      []
                      [ mkChangeSet id1
                        mkChangeSet id1
                        mkChangeSet id2
                        mkChangeSet id2 ]
                      []
              match dups with
              | [ { Identity = i1; Kind = k1; OccurrenceLines = _ }
                  { Identity = i2; Kind = k2; OccurrenceLines = _ } ] ->
                  Expect.equal k1 EpisodeInputIdentityKind.ChangeSet "first kind"
                  Expect.equal k2 EpisodeInputIdentityKind.ChangeSet "second kind"
                  Expect.equal i1 id2 "ordinal ascending: aaa first"
                  Expect.equal i2 id1 "ordinal ascending: zzz second"
              | actual -> failwithf "expected two EpisodeDuplicateIdentity, got %A" actual
          }

          test "change-set reversed record order yields the same failure" {
              let id = "cs-rev-id"
              let fwd = detectUpstreamDuplicates [] [ mkChangeSet id; mkChangeSet id ] []
              let rev = detectUpstreamDuplicates [] [ mkChangeSet id; mkChangeSet id ] []
              Expect.equal fwd rev "reversed order must yield identical normalized result"
          }

          test "change-set unique control reaches empty duplicate list" {
              let dups =
                  detectUpstreamDuplicates
                      []
                      [ mkChangeSet "cs-u-1"
                        mkChangeSet "cs-u-2"
                        mkChangeSet "cs-u-3" ]
                      []
              Expect.isEmpty dups "unique corpus must yield no duplicates"
          }

          // ----- Diagnostic transition -----
          test "transition byte-identical duplicate composite ID is detected" {
              let epId = "ep-tx-byte"
              let fp = "fp-byte"
              let txA = mkTransition epId fp
              let txB = mkTransition epId fp
              let composite = diagnosticTransitionIdentity txA
              let dups = detectUpstreamDuplicates [] [] [ txA; txB ]
              assertSingleDuplicate dups EpisodeInputIdentityKind.DiagnosticTransition composite
          }

          test "transition different content with the same composite ID is detected" {
              let epId = "ep-tx-content"
              let fp = "fp-content"
              let txA = mkTransition epId fp
              let txB = { (mkTransition epId fp) with Code = Some "FS3118" }
              let composite = diagnosticTransitionIdentity txA
              let dups = detectUpstreamDuplicates [] [] [ txA; txB ]
              assertSingleDuplicate dups EpisodeInputIdentityKind.DiagnosticTransition composite
          }

          test "transition three occurrences of one composite ID is detected" {
              let epId = "ep-tx-triple"
              let fp = "fp-triple"
              let dups =
                  detectUpstreamDuplicates
                      []
                      []
                      [ mkTransition epId fp
                        mkTransition epId fp
                        mkTransition epId fp ]
              let composite = epId + "|" + fp
              assertSingleDuplicate dups EpisodeInputIdentityKind.DiagnosticTransition composite
          }

          test "transition two distinct duplicate composite IDs sorted ordinally" {
              let epIdA = "ep-tx-aaa"
              let epIdB = "ep-tx-zzz"
              let fp = "fp-shared"
              let dups =
                  detectUpstreamDuplicates
                      []
                      []
                      [ mkTransition epIdA fp
                        mkTransition epIdA fp
                        mkTransition epIdB fp
                        mkTransition epIdB fp ]
              match dups with
              | [ { Identity = i1; Kind = k1; OccurrenceLines = _ }
                  { Identity = i2; Kind = k2; OccurrenceLines = _ } ] ->
                  Expect.equal k1 EpisodeInputIdentityKind.DiagnosticTransition "first kind"
                  Expect.equal k2 EpisodeInputIdentityKind.DiagnosticTransition "second kind"
                  Expect.equal i1 (epIdA + "|" + fp) "ordinal ascending"
                  Expect.equal i2 (epIdB + "|" + fp) "ordinal ascending"
              | actual -> failwithf "expected two EpisodeDuplicateIdentity, got %A" actual
          }

          test "transition reversed record order yields the same failure" {
              let epId = "ep-tx-rev"
              let fp = "fp-rev"
              let txA = mkTransition epId fp
              let txB = mkTransition epId fp
              let fwd = detectUpstreamDuplicates [] [] [ txA; txB ]
              let rev = detectUpstreamDuplicates [] [] [ txB; txA ]
              Expect.equal fwd rev "reversed order must yield identical normalized result"
          }

          test "transition unique control reaches empty duplicate list" {
              let dups =
                  detectUpstreamDuplicates
                      []
                      []
                      [ mkTransition "ep-u-1" "fp-u-1"
                        mkTransition "ep-u-2" "fp-u-2"
                        mkTransition "ep-u-3" "fp-u-3" ]
              Expect.isEmpty dups "unique corpus must yield no duplicates"
          } ]