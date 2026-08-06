module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateIdentityFailureTests

// =============================================================================
// Rule Candidate Identity Failure Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Twelve tests covering every identity-bearing field in the current
// domain model.  Every duplicate-identity test asserts an exact typed
// `DuplicateInputIdentities` failure with the EXACT identity string
// that the production code computes.  All "byte-identical" duplicate
// tests write a valid corpus first, then append a second copy of the
// SAME identity record, so the duplicate branch is reached.
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private episodesRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/repair-episodes-v1.jsonl"
let private changeSetsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/git-change-sets-v1.jsonl"
let private transitionsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/diagnostic-transitions-v1.jsonl"
let private evidenceRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/verification-evidence-v1.jsonl"

let private afterCommit = String.replicate 40 "b"
let private afterTree = String.replicate 40 "d"

/// Asserts the result contains exactly one `DuplicateInputIdentities` with
/// the supplied kind and identity.  Records the actual error payload
/// first to support post-mortem debugging.
let assertExactDuplicate
    (expectedKind: InputIdentityKind)
    (expectedIdentity: string)
    (errors: EngineError list)
    : unit =
    match errors with
    | [ DuplicateInputIdentities(actualKind, [actualIdentity]) ] ->
        Expect.equal actualKind expectedKind "identity kind"
        Expect.equal actualIdentity expectedIdentity "duplicate identity"
    | actual ->
        failwithf
            "expected DuplicateInputIdentities(%A, [%s]), got actual=%A"
            expectedKind
            expectedIdentity
            actual

[<Tests>]
let identityFailureTests =
    testList
        "FSharpDiagnostics.RuleCandidates.IdentityFailures"
        [ test "empty repair-episode ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-ep"
              let abs = repo.Absolute episodesRel
              let lines = System.IO.File.ReadAllLines(abs)
              lines.[0] <- lines.[0].Replace("\"episode_id\":\""
                  + (System.Text.RegularExpressions.Regex.Escape (deterministicSha256 "rule-candidate-fixture-episode-v1" "id-empty-ep"))
                  + "\"", "\"episode_id\":\"\"")
              System.IO.File.WriteAllLines(abs, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "empty episode id must NOT yield a candidate"
              Expect.isFalse (List.isEmpty r.Errors) "empty episode id must surface an error"
          }

          test "duplicate repair-episode ID (byte-identical records) is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-ep-same"
              let dup = mkValidRepairEpisodeJson "id-dup-ep-same"
              repo.AppendUtf8(episodesRel, dup + "\n")
              let r = extractCandidates repo.Root
              let expectedId = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-dup-ep-same"
              try
                  assertExactDuplicate EpisodeIdentity expectedId r.Errors
              with
              | _ -> failwithf "actual errors: %A" r.Errors
          }

          test "duplicate repair-episode ID (different content, same id) is rejected" {
              use repo = new TempRepository()
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-dup-ep-content"
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "id-dup-ep-content"
              let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" "id-dup-ep-content"
              let a = mkRepairEpisodeJsonWithId epId "fsb-id-dup-ep-content-a" csId [ evidId ]
              let b = mkRepairEpisodeJsonWithId epId "fsb-id-dup-ep-content-b" csId [ evidId ]
              repo.WriteUtf8(episodesRel, a + "\n" + b + "\n")
              repo.WriteUtf8(changeSetsRel, mkChangeSetJsonWithId csId "a.fs" + "\n")
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId evidId epId "pass" 0 afterCommit afterTree + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "different-content duplicate episode id must NOT yield a candidate"
          }

          test "duplicate episode key under different episode IDs is rejected" {
              use repo = new TempRepository()
              let epIdA = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-dup-key-a"
              let epIdB = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-dup-key-b"
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "id-dup-key"
              let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" "id-dup-key"
              let a = mkRepairEpisodeJsonWithId epIdA "fsb-shared-key" csId [ evidId ]
              let b = mkRepairEpisodeJsonWithId epIdB "fsb-shared-key" csId [ evidId ]
              repo.WriteUtf8(episodesRel, a + "\n" + b + "\n")
              repo.WriteUtf8(changeSetsRel, mkChangeSetJsonWithId csId "a.fs" + "\n")
              repo.WriteUtf8(evidenceRel, mkVerificationEvidenceJsonWithId evidId epIdA "pass" 0 afterCommit afterTree + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "duplicate episode key under different ids must NOT yield a candidate"
          }

          test "empty change-set ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-cs"
              repo.WriteUtf8(changeSetsRel,
                  "{\"schema_version\":\"git-change-set-v1\",\"change_set_id\":\"\",\"change_set_version\":\"git-change-set-v1\",\"before_tree_oid\":\"" + (String.replicate 40 "c") + "\",\"after_tree_oid\":\"" + (String.replicate 40 "d") + "\",\"object_format\":\"sha1\",\"entries\":[{\"before_mode\":\"100644\",\"after_mode\":\"100644\",\"before_blob_oid\":null,\"after_blob_oid\":null,\"change_kind\":\"modified\",\"canonical_path\":\"a.fs\"}]}\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "empty change-set id must NOT yield a candidate"
          }

          test "duplicate change-set ID (identical records) is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-cs-same"
              let dup = mkValidChangeSetJson "id-dup-cs-same" "a.fs"
              repo.AppendUtf8(changeSetsRel, dup + "\n")
              let r = extractCandidates repo.Root
              let expectedId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "id-dup-cs-same"
              try
                  assertExactDuplicate ChangeSetIdentity expectedId r.Errors
              with
              | _ -> failwithf "actual errors: %A" r.Errors
          }

          test "duplicate change-set ID (different content, same id) is rejected" {
              use repo = new TempRepository()
              let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" "id-dup-cs-content"
              let a = mkChangeSetJsonWithId csId "a.fs"
              let b = mkChangeSetJsonWithId csId "b.fs"
              repo.WriteUtf8(changeSetsRel, a + "\n" + b + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "different-content duplicate change-set id must NOT yield a candidate"
          }

          test "empty transition ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-tx"
              let abs = repo.Absolute transitionsRel
              let lines = System.IO.File.ReadAllLines(abs)
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-empty-tx"
              let mutated =
                  "{\"schema_version\":\"diagnostic-transition-v1\",\"episode_id\":\"" + epId
                  + "\",\"exact_fingerprint\":\"\","
                  + "\"transition_kind\":\"eliminated_after\",\"before_occurrence_count\":1,\"after_occurrence_count\":0,"
                  + "\"severity\":\"error\",\"code\":\"FS0010\",\"message_normalized\":\"msg\","
                  + "\"source_path\":\"a.fs\",\"project_path\":null,"
                  + "\"span\":{\"start_line\":1,\"start_column\":1,\"end_line\":1,\"end_column\":10},"
                  + "\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},"
                  + "\"source_link\":{\"kind\":\"source_file_modified\",\"paths\":[\"a.fs\"],\"reasons\":[]},"
                  + "\"assessment\":\"observed_resolution_candidate\"}"
              lines.[0] <- mutated
              System.IO.File.WriteAllLines(abs, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "empty transition id must NOT yield a candidate"
          }

          test "duplicate transition ID (identical records) is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-tx-same"
              let dup = mkValidDiagnosticTransitionJson "id-dup-tx-same" "FS0010" "a.fs"
              repo.AppendUtf8(transitionsRel, dup + "\n")
              let r = extractCandidates repo.Root
              // Production transition identity is episode_id + "|" + exact_fingerprint.
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-dup-tx-same"
              let expectedIdentity = epId + "|fp-id-dup-tx-same-FS0010-a.fs"
              try
                  assertExactDuplicate TransitionIdentity expectedIdentity r.Errors
              with
              | _ -> failwithf "actual errors: %A" r.Errors
          }

          test "duplicate transition ID (different content, same id) is rejected" {
              use repo = new TempRepository()
              let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" "id-dup-tx-content"
              let fp = "shared-fp"
              let path = "a.fs"
              let code1 = "FS0010"
              let code2 = "FS3118"
              let mk (code: string) (kind: string) =
                  "{\"schema_version\":\"diagnostic-transition-v1\","
                  + "\"episode_id\":\"" + epId + "\","
                  + "\"exact_fingerprint\":\"" + fp + "\","
                  + "\"transition_kind\":\"" + kind + "\","
                  + "\"before_occurrence_count\":1,\"after_occurrence_count\":0,"
                  + "\"severity\":\"error\",\"code\":\"" + code + "\","
                  + "\"message_normalized\":\"msg\","
                  + "\"source_path\":\"" + path + "\",\"project_path\":null,"
                  + "\"span\":{\"start_line\":1,\"start_column\":1,\"end_line\":1,\"end_column\":10},"
                  + "\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},"
                  + "\"source_link\":{\"kind\":\"source_file_modified\",\"paths\":[\"" + path + "\"],\"reasons\":[]},"
                  + "\"assessment\":\"observed_resolution_candidate\"}"
              repo.WriteUtf8(transitionsRel, mk code1 "eliminated_after" + "\n" + mk code2 "persisted_same_count" + "\n")
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "different-content duplicate transition must NOT yield a candidate"
          }

          test "empty verification-evidence ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-empty-ev"
              let abs = repo.Absolute evidenceRel
              let lines = System.IO.File.ReadAllLines(abs)
              lines.[0] <- lines.[0].Replace("\"evidence_id\":\""
                  + (System.Text.RegularExpressions.Regex.Escape (deterministicSha256 "rule-candidate-fixture-evidence-v1" "id-empty-ev"))
                  + "\"", "\"evidence_id\":\"\"")
              System.IO.File.WriteAllLines(abs, lines)
              let r = extractCandidates repo.Root
              Expect.equal r.Candidates.Length 0 "empty evidence id must NOT yield a candidate"
          }

          test "duplicate verification-evidence ID is rejected" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "id-dup-ev-same"
              let dup = mkValidVerificationEvidenceJson "id-dup-ev-same" "pass" 0
              repo.AppendUtf8(evidenceRel, dup + "\n")
              let r = extractCandidates repo.Root
              let expectedId = deterministicSha256 "rule-candidate-fixture-evidence-v1" "id-dup-ev-same"
              try
                  assertExactDuplicate VerificationEvidenceIdentity expectedId r.Errors
              with
              | _ -> failwithf "actual errors: %A" r.Errors
          } ]