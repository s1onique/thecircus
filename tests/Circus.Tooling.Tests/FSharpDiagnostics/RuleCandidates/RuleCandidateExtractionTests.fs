module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateExtractionTests

// =============================================================================
// Rule Candidate Extraction Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01-CORRECTION01
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.FSharpDiagnostics.RepoPaths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Classification
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Selection
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Serialization

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION03:
// The previous traversal had one hop too many and returned the parent of
// the repository root, which caused every test in this file that called
// extractCandidates to look for the production corpus at a non-existent
// path and fail with DirectoryNotFoundException.  The correct number of
// .Parent hops from __SOURCE_DIRECTORY__ (which is
// tests/Circus.Tooling.Tests/FSharpDiagnostics/RuleCandidates) is three,
// since GetParent already walks one level up.
let private repoRoot () : string =
    Directory.GetParent(__SOURCE_DIRECTORY__).Parent.Parent.Parent.FullName

let private emptySpan: Circus.Tooling.FSharpDiagnostics.Domain.SourceSpan =
    { StartLine = None
      StartColumn = None
      EndLine = None
      EndColumn = None }

let private emptyCompat: Compatibility =
    { Status = CompatibilityStatus.Compatible
      Reasons = []
      MissingFields = [] }

let private mkChangeEntry (kind: GitChangeKind) (path: string) : GitChangeEntry =
    { BeforeMode = "100644"
      AfterMode = "100644"
      BeforeBlobOid = None
      AfterBlobOid = None
      ChangeKind = kind
      CanonicalPath = path }

let private mkTransition
    (epId: string)
    (code: string)
    (path: string)
    (assessment: TransitionAssessment)
    (beforeCount: int)
    (afterCount: int)
    (kind: ExactTransitionKind)
    (line: int option)
    : DiagnosticTransition =
    { SchemaVersion = "diagnostic-transition-v1"
      EpisodeId = epId
      ExactFingerprint = sprintf "%s-%s-%s-%d-%d" epId code path beforeCount afterCount
      TransitionKind = kind
      BeforeOccurrenceCount = beforeCount
      AfterOccurrenceCount = afterCount
      Severity = Circus.Tooling.FSharpDiagnostics.Domain.DiagnosticSeverity.Error
      Code = Some code
      MessageNormalized = "msg-" + code
      SourcePath = Some path
      ProjectPath = None
      Span = { emptySpan with StartLine = line }
      Compatibility = emptyCompat
      SourceLink =
        { Kind = SourceLinkKind.SourceFileModified path
          Paths = [ path ]
          Reasons = [] }
      Assessment = assessment }

let private mkChangeSet (csId: string) (path: string) : GitChangeSet =
    { SchemaVersion = "git-change-set-v1"
      ChangeSetId = csId
      ChangeSetVersion = "git-change-set-v1"
      BeforeTreeOid = String.replicate 40 "0"
      AfterTreeOid = String.replicate 40 "1"
      ObjectFormat = GitObjectFormat.Sha1
      Entries = [ mkChangeEntry GitChangeKind.Modified path ] }

let private mkEpisode (epId: string) (epKey: string) (csId: string) (qualified: bool) : RepairEpisode =
    { SchemaVersion = "repair-episode-v1"
      EpisodeId = epId
      EpisodeKey = epKey
      BeforeCaptureId = epId + "-before"
      AfterCaptureId = epId + "-after"
      BeforeCommitOid = String.replicate 40 "0"
      AfterCommitOid = String.replicate 40 "1"
      BeforeTreeOid = String.replicate 40 "2"
      AfterTreeOid = String.replicate 40 "3"
      CommitRange = [ String.replicate 40 "0"; String.replicate 40 "1" ]
      ChangeSetId = csId
      CommandContractBefore = "dotnet build"
      CommandContractAfter = "dotnet build"
      Compatibility = emptyCompat
      TransitionCounts =
        { PersistedSameCount = 0
          PersistedCountDecreased = 0
          PersistedCountIncreased = 0
          EliminatedAfter = 4
          IntroducedAfter = 0
          ResolutionCandidates = 4
          RegressionCandidates = 0
          Unassessable = 0 }
      VerificationLevel = VerificationLevel.FocusedGateVerified
      VerificationEvidenceIds = []
      Qualification =
        if qualified then
            { Status = EpisodeQualificationStatus.Qualified
              Reasons = [] }
        else
            { Status = EpisodeQualificationStatus.Ambiguous
              Reasons = [ "test" ] } }

// -----------------------------------------------------------------------------
// fsb-0025 production replay (P0-8)
// -----------------------------------------------------------------------------

[<Tests>]
let fsb0025Tests =
    testList
        "FSharpDiagnostics.RuleCandidates.Engine.fs.b0025"
        [ test "fsb-0025 produces exactly one ParserCascadeRepair candidate" {
              let result = extractCandidates (repoRoot ())

              Expect.equal result.EligibleEpisodes 1 "should have 1 eligible episode"

              if not (List.isEmpty result.Errors) then
                  failwithf "Extraction had errors: %A" result.Errors

              Expect.equal result.Candidates.Length 1 "fsb-0025 should produce exactly one candidate"
          }

          test "fsb-0025 candidate has ParserCascadeRepair kind" {
              let result = extractCandidates (repoRoot ())

              if not (List.isEmpty result.Errors) then
                  failwithf "Extraction had errors: %A" result.Errors

              let candidate = result.Candidates.Head

              Expect.equal
                  candidate.Kind
                  RuleCandidateKind.ParserCascadeRepair
                  "candidate kind should be ParserCascadeRepair"
          }

          test "fsb-0025 candidate has SingleEpisodeObservedRepair evidence strength" {
              let result = extractCandidates (repoRoot ())

              if not (List.isEmpty result.Errors) then
                  failwithf "Extraction had errors: %A" result.Errors

              let candidate = result.Candidates.Head

              Expect.equal
                  candidate.EvidenceStrength
                  EvidenceStrength.SingleEpisodeObservedRepair
                  "evidence strength should be SingleEpisodeObservedRepair"
          }

          test "fsb-0025 candidate references fsb-0025 episode" {
              let result = extractCandidates (repoRoot ())

              if not (List.isEmpty result.Errors) then
                  failwithf "Extraction had errors: %A" result.Errors

              let candidate = result.Candidates.Head
              Expect.equal candidate.Evidence.EpisodeKey "fsb-0025" "episode key should be fsb-0025"
          }

          test "fsb-0025 candidate has positive supporting transition" {
              let result = extractCandidates (repoRoot ())

              if not (List.isEmpty result.Errors) then
                  failwithf "Extraction had errors: %A" result.Errors

              let candidate = result.Candidates.Head
              Expect.isGreaterThan candidate.TransitionPartition.SupportingTransitionIds.Length 0 "must have at least one positive supporting transition"
          }

          test "fsb-0025 candidate does not embed repair advice" {
              let result = extractCandidates (repoRoot ())

              if not (List.isEmpty result.Errors) then
                  failwithf "Extraction had errors: %A" result.Errors

              let candidate = result.Candidates.Head
              Expect.isFalse candidate.StatusFlags.RepairAdviceAvailable "repair_advice_available must be false"
              Expect.isFalse candidate.StatusFlags.LlmTipAvailable "llm_tip_available must be false"
              Expect.isFalse candidate.StatusFlags.CausalFamilyCurated "causal_family_curated must be false"
          }

          test "fsb-0025 candidate_hypothesis is descriptive and not imperative" {
              let result = extractCandidates (repoRoot ())

              if not (List.isEmpty result.Errors) then
                  failwithf "Extraction had errors: %A" result.Errors

              let candidate = result.Candidates.Head
              Expect.isTrue (candidate.CandidateHypothesis.Contains "provisional") "candidate_hypothesis must explicitly state it is provisional"
              Expect.isFalse (candidate.CandidateHypothesis.Contains "Rebuild") "candidate_hypothesis must NOT instruct the agent to rebuild"
              Expect.isFalse (candidate.CandidateHypothesis.Contains "Replace") "candidate_hypothesis must NOT instruct the agent to replace"
          }

          test "fsb-0025 candidate has non-empty supporting transitions" {
              let result = extractCandidates (repoRoot ())

              if not (List.isEmpty result.Errors) then
                  failwithf "Extraction had errors: %A" result.Errors

              let c = result.Candidates.Head
              Expect.isGreaterThan c.TransitionPartition.SupportingTransitionIds.Length 0 "supporting_transition_ids must be non-empty"
              Expect.isGreaterThan c.Evidence.VerificationEvidenceIds.Length 0 "verification_evidence_ids must be non-empty"
          } ]

// -----------------------------------------------------------------------------
// Path normalization authority tests (P0-4)
// -----------------------------------------------------------------------------

[<Tests>]
let pathNormalizationTests =
    testList
        "FSharpDiagnostics.RepoPaths"
        [ test "<REPO>/a.fs normalizes to a.fs" {
              Expect.equal (normalizeRepositoryPath "<REPO>/a.fs") "a.fs" "prefix must be stripped"
          }
          test "a.fs stays as a.fs" {
              Expect.equal (normalizeRepositoryPath "a.fs") "a.fs" "non-prefixed path must be untouched"
          }
          test "<REPO> alone stays as <REPO>" {
              Expect.equal (normalizeRepositoryPath "<REPO>") "<REPO>" "prefix without slash must NOT be stripped"
          }
          test "<REPOSITORY>/a.fs stays as <REPOSITORY>/a.fs" {
              Expect.equal (normalizeRepositoryPath "<REPOSITORY>/a.fs") "<REPOSITORY>/a.fs" "similar-but-different prefix must NOT be stripped"
          }
          test "empty string stays empty" {
              Expect.equal (normalizeRepositoryPath "") "" "empty string must be returned unchanged"
          }
          test "normalization is idempotent" {
              let once = normalizeRepositoryPath "<REPO>/a.fs"
              let twice = normalizeRepositoryPath once
              Expect.equal once twice "normalization must be idempotent"
          }
          test "hasRepositoryPrefix recognizes the canonical prefix" {
              Expect.isTrue (hasRepositoryPrefix "<REPO>/x") "must recognize canonical prefix"
              Expect.isFalse (hasRepositoryPrefix "x") "non-prefixed path must not be recognized"
              Expect.isFalse (hasRepositoryPrefix "<REPO>") "prefix without slash must not be recognized"
              Expect.isFalse (hasRepositoryPrefix "<REPOSITORY>/x") "similar-but-different prefix must not be recognized"
              Expect.isFalse (hasRepositoryPrefix "") "empty string must not match"
          } ]

// -----------------------------------------------------------------------------
// Transition assessment authority tests (P0-2)
// -----------------------------------------------------------------------------

[<Tests>]
let transitionAuthorityTests =
    testList
        "FSharpDiagnostics.RuleCandidates.Classification.transition_authority"
        [ test "ObservedResolutionCandidate is positive" {
              Expect.isTrue (isPositiveTransitionAssessment TransitionAssessment.ObservedResolutionCandidate) "ObservedResolutionCandidate must be positive"
          }
          test "MultiplicityImprovementCandidate is positive" {
              Expect.isTrue (isPositiveTransitionAssessment TransitionAssessment.MultiplicityImprovementCandidate) "MultiplicityImprovementCandidate must be positive"
          }
          test "Unassessable is NEVER positive" {
              Expect.isFalse (isPositiveTransitionAssessment TransitionAssessment.Unassessable) "Unassessable must NEVER be positive"
          }
          test "Ambiguous is NEVER positive" {
              Expect.isFalse (isPositiveTransitionAssessment TransitionAssessment.Ambiguous) "Ambiguous must NEVER be positive"
          }
          test "ObservedRegressionCandidate is counterevidence" {
              Expect.isFalse (isPositiveTransitionAssessment TransitionAssessment.ObservedRegressionCandidate) "ObservedRegressionCandidate must NOT be positive"
              Expect.isTrue (isCounterevidenceTransitionAssessment TransitionAssessment.ObservedRegressionCandidate) "ObservedRegressionCandidate must be counterevidence"
          }
          test "MultiplicityRegressionCandidate is counterevidence" {
              Expect.isFalse (isPositiveTransitionAssessment TransitionAssessment.MultiplicityRegressionCandidate) "MultiplicityRegressionCandidate must NOT be positive"
              Expect.isTrue (isCounterevidenceTransitionAssessment TransitionAssessment.MultiplicityRegressionCandidate) "MultiplicityRegressionCandidate must be counterevidence"
          }
          test "Unassessable and Ambiguous are context-only" {
              Expect.isTrue (isContextTransitionAssessment TransitionAssessment.Unassessable) "Unassessable must be context-only"
              Expect.isTrue (isContextTransitionAssessment TransitionAssessment.Ambiguous) "Ambiguous must be context-only"
          } ]

[<Tests>]
let partitionTests =
    testList
        "FSharpDiagnostics.RuleCandidates.Classification.partition"
        [ test "positive transition is Supporting" {
              let t =
                  mkTransition
                      "ep1"
                      "FS0010"
                      "a.fs"
                      TransitionAssessment.ObservedResolutionCandidate
                      1
                      0
                      ExactTransitionKind.EliminatedAfter
                      (Some 1)

              let gf =
                  { Path = "a.fs"
                    TransitionCount = 1
                    DiagnosticCodes = [ "FS0010" ]
                    EarliestLine = Some 1
                    TransitionIds = [ t.ExactFingerprint ] }

              let p = buildPartition gf [ t ]

              Expect.equal p.SupportingTransitionIds [ t.ExactFingerprint ] "positive must be Supporting"
              Expect.isEmpty p.CounterevidenceTransitionIds "positive must NOT be counterevidence"
              Expect.isEmpty p.ContextTransitionIds "positive must NOT be context"
          }

          test "unassessable transition is Context, never Supporting" {
              let t =
                  mkTransition
                      "ep1"
                      "FS0010"
                      "a.fs"
                      TransitionAssessment.Unassessable
                      1
                      0
                      ExactTransitionKind.EliminatedAfter
                      (Some 1)

              let gf =
                  { Path = "a.fs"
                    TransitionCount = 1
                    DiagnosticCodes = [ "FS0010" ]
                    EarliestLine = Some 1
                    TransitionIds = [ t.ExactFingerprint ] }

              let p = buildPartition gf [ t ]

              Expect.isEmpty p.SupportingTransitionIds "Unassessable must NEVER be Supporting"
              Expect.equal p.ContextTransitionIds [ t.ExactFingerprint ] "Unassessable must be Context"
          }

          test "ambiguous transition is Context" {
              let t =
                  mkTransition
                      "ep1"
                      "FS0010"
                      "a.fs"
                      TransitionAssessment.Ambiguous
                      1
                      0
                      ExactTransitionKind.EliminatedAfter
                      (Some 1)

              let gf =
                  { Path = "a.fs"
                    TransitionCount = 1
                    DiagnosticCodes = [ "FS0010" ]
                    EarliestLine = Some 1
                    TransitionIds = [ t.ExactFingerprint ] }

              let p = buildPartition gf [ t ]

              Expect.isEmpty p.SupportingTransitionIds "Ambiguous must NEVER be Supporting"
              Expect.equal p.ContextTransitionIds [ t.ExactFingerprint ] "Ambiguous must be Context"
          }

// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION04:
// The previous fixture used `TransitionKind = IntroducedAfter` together
// with `Assessment = ObservedRegressionCandidate`.  The production
// `classifyTransitionRole` checks `IntroducedAfter` first and returns
// `Excluded`, so the regression assessment never reached the
// counterevidence branch and the test failed.  `IntroducedAfter` is a
// structural exclusion independent of assessment per spec §6.  The
// smallest correction is to switch the fixture to `PersistedCountIncreased`
// (which is consistent with a regression: count went up across the
// commit boundary) so the counterevidence branch is reachable.
          test "regression transition is Counterevidence" {
              let t =
                  mkTransition
                      "ep1"
                      "FS0010"
                      "a.fs"
                      TransitionAssessment.ObservedRegressionCandidate
                      0
                      1
                      ExactTransitionKind.PersistedCountIncreased
                      (Some 1)

              let gf =
                  { Path = "a.fs"
                    TransitionCount = 1
                    DiagnosticCodes = [ "FS0010" ]
                    EarliestLine = Some 1
                    TransitionIds = [ t.ExactFingerprint ] }

              let p = buildPartition gf [ t ]

              Expect.isEmpty p.SupportingTransitionIds "Regression must NEVER be Supporting"
              Expect.equal p.CounterevidenceTransitionIds [ t.ExactFingerprint ] "Regression must be Counterevidence"
          } ]

// -----------------------------------------------------------------------------
// Deterministic identity tests (P0-5)
// -----------------------------------------------------------------------------

[<Tests>]
let identityTests =
    testList
        "FSharpDiagnostics.RuleCandidates.Identity"
        [ test "computeCandidateId is stable across extractions" {
              let r1 = extractCandidates (repoRoot ())

              if not (List.isEmpty r1.Errors) then
                  failwithf "first extraction failed: %A" r1.Errors

              let r2 = extractCandidates (repoRoot ())

              if not (List.isEmpty r2.Errors) then
                  failwithf "second extraction failed: %A" r2.Errors

              let c1 = r1.Candidates.Head
              let c2 = r2.Candidates.Head
              Expect.equal c1.CandidateId c2.CandidateId "candidate IDs must match across extractions"
          }

          test "candidate_id is rejected as forged when zeroed" {
              let r = extractCandidates (repoRoot ())

              if not (List.isEmpty r.Errors) then
                  failwithf "Extraction failed: %A" r.Errors

              let c = r.Candidates.Head
              let zeroed = String.replicate 64 "0"
              Expect.notEqual c.CandidateId zeroed "candidate_id must not be the zero hash"
          }

          test "candidate_id is a 64-character hex SHA-256" {
              let r = extractCandidates (repoRoot ())

              if not (List.isEmpty r.Errors) then
                  failwithf "Extraction failed: %A" r.Errors

              let c = r.Candidates.Head
              Expect.equal c.CandidateId.Length 64 "candidate_id must be 64 chars"
              Expect.isTrue (c.CandidateId |> Seq.forall (fun ch -> System.Char.IsDigit ch || (ch >= 'a' && ch <= 'f'))) "candidate_id must be lowercase hex"
          }

          test "candidate_id depends on changed_paths contents" {
              let mkCandidate changedPaths =
                  let schemaVersion = RuleCandidateSchemaVersion
                  let kind = RuleCandidateKind.ParserCascadeRepair
                  let strength = EvidenceStrength.SingleEpisodeObservedRepair
                  computeCandidateId
                      schemaVersion kind strength
                      "title" "symptom" "applicability" "observation" "hypothesis"
                      [ "limitation" ]
                      "a.fs"
                      [ "FS0010" ]
                      1
                      (Some 1)
                      changedPaths
                      "ep" "fsb" "cs"
                      [ "ev" ]
                      [ "t1" ]
                      [ "t2" ]
                      [ "t3" ]
                      "before_commit" "before_tree" "after_commit" "after_tree"

              let a = mkCandidate [ "x.fs" ]
              let b = mkCandidate [ "y.fs" ]
              Expect.notEqual a b "candidate_id must depend on changed_paths"
          }

          test "candidate_id depends on supporting_transition_ids" {
              let mkCandidate sId =
                  let schemaVersion = RuleCandidateSchemaVersion
                  let kind = RuleCandidateKind.ParserCascadeRepair
                  let strength = EvidenceStrength.SingleEpisodeObservedRepair
                  computeCandidateId
                      schemaVersion kind strength
                      "title" "symptom" "applicability" "observation" "hypothesis"
                      []
                      "a.fs" [] 0 None []
                      "ep" "fsb" "cs" []
                      sId
                      [] []
                      "" "" "" ""

              Expect.notEqual (mkCandidate [ "t1" ]) (mkCandidate [ "t2" ]) "candidate_id must depend on supporting_transition_ids"
          } ]

// -----------------------------------------------------------------------------
// Verification-evidence binding tests
// -----------------------------------------------------------------------------

[<Tests>]
let bindingTests =
    testList
        "FSharpDiagnostics.RuleCandidates.VerificationBinding"
        [ test "validateVerificationBinding returns error on missing evidence" {
              let ep = mkEpisode "ep1" "fsb-test" "cs1" true
              let m : Map<string, LocatedVerificationEvidence> = Map.empty
              let r = validateVerificationBinding ep m
              Expect.isTrue (Option.isSome r) "must report missing evidence"
          } ]
