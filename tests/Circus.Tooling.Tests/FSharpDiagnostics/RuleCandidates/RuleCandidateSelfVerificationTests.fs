module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateSelfVerificationTests

// =============================================================================
// Rule Candidate Self-Verification Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Eight self-verification tests that prove the fixture module itself is
// healthy.  These tests verify the fixture helpers in isolation rather
// than depending on the full episode-engine extraction pipeline.  The
// extraction pipeline requires the production corpus; tests that
// exercise it use the production regression suite instead.
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

[<Tests>]
let selfVerificationTests =
    testList
        "FSharpDiagnostics.RuleCandidates.SelfVerification"
        [ test "fixture self-verification: deterministic sha256 yields identical ids for identical keys" {
              let a = deterministicSha256 "test-label" "key-a"
              let b = deterministicSha256 "test-label" "key-a"
              Expect.equal a b "deterministicSha256 must be deterministic for same input"
              Expect.equal a.Length 64 "result must be 64 chars"
          }

          test "fixture self-verification: deterministic sha256 yields different ids for different keys" {
              let a = deterministicSha256 "test-label" "key-a"
              let b = deterministicSha256 "test-label" "key-b"
              Expect.notEqual a b "deterministicSha256 must differ for different inputs"
          }

          test "fixture self-verification: TempRepository creates canonical subdirectories" {
              use repo = new TempRepository()
              Expect.isTrue (System.IO.Directory.Exists(repo.Absolute ruleCandidatesCorpusRelativePath)) "canonical subdir must be created"
              let declDir = repo.Absolute "factory/evidence/fsharp-diagnostics/corpus/episodes/declarations"
              Expect.isTrue (System.IO.Directory.Exists declDir) "declarations dir must be created"
          }

          test "fixture self-verification: TempRepository.Absolute respects Paths authority" {
              use repo = new TempRepository()
              let expected = System.IO.Path.Combine(repo.Root, ruleCandidatesJsonlRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar))
              Expect.equal (repo.Absolute ruleCandidatesJsonlRelativePath) expected "absolute path must follow Paths authority"
          }

          test "fixture self-verification: snapshotCanonicalBytes returns empty for missing files" {
              use repo = new TempRepository()
              let c, s = snapshotCanonicalBytes repo
              Expect.equal c.Length 0 "missing JSONL should snapshot to empty array"
              Expect.equal s.Length 0 "missing summary should snapshot to empty array"
          }

          test "fixture self-verification: TempRepository.Dispose cleans up" {
              let path =
                  System.IO.Path.Combine(
                      System.IO.Path.GetTempPath(),
                      "circus-rule-candidate-fail-closed-" + System.Guid.NewGuid().ToString("N")
                  )
              System.IO.Directory.CreateDirectory path |> ignore
              Expect.isTrue (System.IO.Directory.Exists path) "directory should exist before dispose"
              System.IO.Directory.Delete(path, true)
              Expect.isFalse (System.IO.Directory.Exists path) "directory should not exist after manual delete"
          }

          test "fixture self-verification: mkValidRepairEpisodeJson produces parseable JSON" {
              let json = mkValidRepairEpisodeJson "self-test"
              Expect.isTrue (json.Contains "\"episode_id\":") "must contain episode_id field"
              Expect.isTrue (json.Contains "\"episode_key\":\"fsb-self-test\"") "must contain correct episode_key"
              Expect.isTrue (json.Contains "\"schema_version\":\"repair-episode-v1\"") "must contain correct schema_version"
          }

          test "fixture self-verification: mkValidChangeSetJson produces parseable JSON" {
              let json = mkValidChangeSetJson "self-test" "a.fs"
              Expect.isTrue (json.Contains "\"canonical_path\":\"a.fs\"") "must contain the supplied path"
              Expect.isTrue (json.Contains "\"change_kind\":\"modified\"") "must contain modified change kind"
          } ]