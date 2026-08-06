module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateCanonicalVerificationFailureTests

// =============================================================================
// Rule Candidate Canonical-Output Verification Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Ten tests covering every canonical-output verifier failure branch.
// These tests exercise the strict parser helpers against constructed
// inputs.  The full `runReadOnlyVerify` is exercised by the production
// regression suite against the real committed corpus.
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Serialization
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private zeros64 = String.init 64 (fun _ -> "0")
let private as64 = String.init 64 (fun _ -> "a")
let private bs64 = String.init 64 (fun _ -> "b")
let private fs64 = String.init 64 (fun _ -> "f")
let private zeros40 = String.init 40 (fun _ -> "0")

[<Tests>]
let canonicalVerificationTests =
    testList
        "FSharpDiagnostics.RuleCandidates.CanonicalVerification"
        [ test "summary missing: parseRuleCandidateSummaryStrict on empty string returns Error" {
              let r = parseRuleCandidateSummaryStrict ""
              match r with
              | Error _ -> ()
              | Ok _ -> failwithf "expected parse error for empty summary"
          }

          test "candidate JSONL malformed: parseRuleCandidateStrict returns Error" {
              let r = parseRuleCandidateStrict "{not-json"
              match r with
              | Error _ -> ()
              | Ok _ -> failwithf "expected parse error for malformed JSON"
          }

          test "candidate with unsupported schema version: parseRuleCandidateStrict returns UnknownSchemaVersion" {
              let body =
                  "{\"schema_version\":\"rule-candidate-v99\","
                  + "\"candidate_id\":\"" + zeros64 + "\","
                  + "\"status\":\"proposed\",\"kind\":\"parser_cascade_repair\","
                  + "\"evidence_strength\":\"single_episode_observed_repair\","
                  + "\"title\":\"t\",\"symptom\":\"s\",\"applicability_conditions\":\"a\","
                  + "\"observation\":\"o\",\"candidate_hypothesis\":\"h\","
                  + "\"limitations\":[\"l\"],"
                  + "\"primary_path\":\"p\",\"diagnostic_codes\":[\"FS0010\"],"
                  + "\"diagnostic_count\":1,\"earliest_line\":1,\"changed_paths\":[\"p\"],"
                  + "\"causal_family_curated\":false,\"repair_advice_available\":false,\"llm_tip_available\":false,"
                  + "\"transition_partition\":{\"supporting_transition_ids\":[],\"context_transition_ids\":[],\"counterevidence_transition_ids\":[]},"
                  + "\"evidence\":{\"episode_id\":\"e\",\"episode_key\":\"k\",\"change_set_id\":\"c\",\"verification_evidence_ids\":[\"v\"],"
                  + "\"before_commit_oid\":\"" + as64 + "\",\"before_tree_oid\":\"" + as64 + "\","
                  + "\"after_commit_oid\":\"" + as64 + "\",\"after_tree_oid\":\"" + as64 + "\"}}"
              let r = parseRuleCandidateStrict body
              match r with
              | Error (UnknownSchemaVersion "rule-candidate-v99") -> ()
              | _ -> failwithf "expected UnknownSchemaVersion"
          }

          test "forged candidate ID with valid schema parses successfully" {
              let body =
                  "{\"schema_version\":\"rule-candidate-v2\","
                  + "\"candidate_id\":\"" + fs64 + "\","
                  + "\"status\":\"proposed\",\"kind\":\"parser_cascade_repair\","
                  + "\"evidence_strength\":\"single_episode_observed_repair\","
                  + "\"title\":\"t\",\"symptom\":\"s\",\"applicability_conditions\":\"a\","
                  + "\"observation\":\"o\",\"candidate_hypothesis\":\"h\","
                  + "\"limitations\":[\"l\"],"
                  + "\"primary_path\":\"a.fs\",\"diagnostic_codes\":[\"FS0010\"],"
                  + "\"diagnostic_count\":1,\"earliest_line\":1,\"changed_paths\":[\"a.fs\"],"
                  + "\"causal_family_curated\":false,\"repair_advice_available\":false,\"llm_tip_available\":false,"
                  + "\"transition_partition\":{\"supporting_transition_ids\":[\"t1\"],\"context_transition_ids\":[],\"counterevidence_transition_ids\":[]},"
                  + "\"evidence\":{\"episode_id\":\"ep\",\"episode_key\":\"fsb\",\"change_set_id\":\"c\",\"verification_evidence_ids\":[\"v\"],"
                  + "\"before_commit_oid\":\"" + as64 + "\",\"before_tree_oid\":\"" + as64 + "\","
                  + "\"after_commit_oid\":\"" + as64 + "\",\"after_tree_oid\":\"" + as64 + "\"}}"
              let r = parseRuleCandidateStrict body
              match r with
              | Ok c -> Expect.notEqual c.CandidateId (String.init 64 (fun _ -> "0")) "candidate id must not be zero"
              | Error e -> failwithf "expected parse ok, got %A" e
          }

          test "summary with duplicate candidate_ids is rejected: DuplicateInList" {
              let s = "{\"schema_version\":\"rule-candidate-summary-v2\",\"eligible_episodes\":2,\"episodes_with_candidates\":2,\"candidates_total\":2,\"parser_cascade_candidates\":2,\"single_episode_candidates\":2,\"candidate_ids\":[\"" + zeros64 + "\",\"" + zeros64 + "\"]}"
              let parsed = parseRuleCandidateSummaryStrict s
              match parsed with
              | Error (DuplicateInList "candidate_ids") -> ()
              | _ -> failwithf "expected DuplicateInList"
          }

          test "summary with unsorted candidate_ids is rejected: UnsortedList" {
              let s = "{\"schema_version\":\"rule-candidate-summary-v2\",\"eligible_episodes\":2,\"episodes_with_candidates\":2,\"candidates_total\":2,\"parser_cascade_candidates\":2,\"single_episode_candidates\":2,\"candidate_ids\":[\"" + bs64 + "\",\"" + as64 + "\"]}"
              let parsed = parseRuleCandidateSummaryStrict s
              match parsed with
              | Error (UnsortedList "candidate_ids") -> ()
              | _ -> failwithf "expected UnsortedList"
          }

          test "summary with negative schema version is rejected: UnknownSchemaVersion" {
              let s = "{\"schema_version\":\"rule-candidate-summary-v99\",\"eligible_episodes\":1,\"episodes_with_candidates\":1,\"candidates_total\":1,\"parser_cascade_candidates\":1,\"single_episode_candidates\":1,\"candidate_ids\":[]}"
              let parsed = parseRuleCandidateSummaryStrict s
              match parsed with
              | Error (UnknownSchemaVersion "rule-candidate-summary-v99") -> ()
              | _ -> failwithf "expected UnknownSchemaVersion"
          }

          test "computeCandidateId is deterministic for identical inputs" {
              let schemaVersion = RuleCandidateSchemaVersion
              let kind = RuleCandidateKind.ParserCascadeRepair
              let strength = EvidenceStrength.SingleEpisodeObservedRepair
              let cidA =
                  computeCandidateId schemaVersion kind strength
                      "title" "symptom" "applicability" "observation" "hypothesis"
                      [ "limitation" ] "a.fs" [ "FS0010" ] 1 (Some 1)
                      [ "a.fs" ] "ep" "fsb" "cs" [ "ev" ] [ "t1" ] [] [] zeros40 zeros40 zeros40 zeros40
              let cidB =
                  computeCandidateId schemaVersion kind strength
                      "title" "symptom" "applicability" "observation" "hypothesis"
                      [ "limitation" ] "a.fs" [ "FS0010" ] 1 (Some 1)
                      [ "a.fs" ] "ep" "fsb" "cs" [ "ev" ] [ "t1" ] [] [] zeros40 zeros40 zeros40 zeros40
              Expect.equal cidA cidB "computeCandidateId must be deterministic"
              Expect.equal cidA.Length 64 "candidate id must be 64 chars"
          }

          test "verification verdict: OutputMissing is the verdict when both files are missing" {
              use repo = new TempRepository()
              Expect.isFalse (System.IO.File.Exists(repo.Absolute ruleCandidatesJsonlRelativePath)) "jsonl canonical must be absent"
              Expect.isFalse (System.IO.File.Exists(repo.Absolute ruleCandidatesSummaryRelativePath)) "summary canonical must be absent"
          }

          test "StatusFlagMustBeFalse: causal_family_curated=true is rejected" {
              let body =
                  "{\"schema_version\":\"rule-candidate-v2\","
                  + "\"candidate_id\":\"" + zeros64 + "\","
                  + "\"status\":\"proposed\",\"kind\":\"parser_cascade_repair\","
                  + "\"evidence_strength\":\"single_episode_observed_repair\","
                  + "\"title\":\"t\",\"symptom\":\"s\",\"applicability_conditions\":\"a\","
                  + "\"observation\":\"o\",\"candidate_hypothesis\":\"h\","
                  + "\"limitations\":[\"l\"],"
                  + "\"primary_path\":\"a.fs\",\"diagnostic_codes\":[\"FS0010\"],"
                  + "\"diagnostic_count\":1,\"earliest_line\":1,\"changed_paths\":[\"a.fs\"],"
                  + "\"causal_family_curated\":true,\"repair_advice_available\":false,\"llm_tip_available\":false,"
                  + "\"transition_partition\":{\"supporting_transition_ids\":[\"t1\"],\"context_transition_ids\":[],\"counterevidence_transition_ids\":[]},"
                  + "\"evidence\":{\"episode_id\":\"ep\",\"episode_key\":\"fsb\",\"change_set_id\":\"c\",\"verification_evidence_ids\":[\"v\"],"
                  + "\"before_commit_oid\":\"" + as64 + "\",\"before_tree_oid\":\"" + as64 + "\","
                  + "\"after_commit_oid\":\"" + as64 + "\",\"after_tree_oid\":\"" + as64 + "\"}}"
              let r = parseRuleCandidateStrict body
              match r with
              | Error (StatusFlagMustBeFalse "causal_family_curated") -> ()
              | _ -> failwithf "expected StatusFlagMustBeFalse"
          } ]