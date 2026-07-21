module Circus.Tooling.Tests.NoForcePush.StaticPolicyTests

open Expecto
open Circus.Tooling.NoForcePush.StaticPolicy
open Circus.Tooling.NoForcePush.Types

[<Tests>]
let tests =
    testList
        "NoForcePush StaticPolicy"
        [ test "detects long force option" {
              let findings = analyzeCommand "git push --force origin main"
              Expect.isNonEmpty findings "has findings"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-001") findings) "NFP-001"
          }
          test "detects short force option" {
              let findings = analyzeCommand "git push -f origin main"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-001") findings) "NFP-001"
          }
          test "detects bundled short force option" {
              let findings = analyzeCommand "git push -uf origin main"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-001") findings) "NFP-001"
          }
          test "detects force-with-lease" {
              let findings = analyzeCommand "git push --force-with-lease origin main"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-001") findings) "NFP-001"
          }
          test "detects force-if-includes" {
              let findings = analyzeCommand "git push --force-if-includes origin main"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-001") findings) "NFP-001"
          }
          test "detects leading-plus refspec" {
              let findings = analyzeCommand "git push origin +main:refs/heads/main"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-002") findings) "NFP-002"
          }
          test "detects remote delete long option" {
              let findings = analyzeCommand "git push --delete origin feature"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-003") findings) "NFP-003"
          }
          test "detects remote delete short option" {
              let findings = analyzeCommand "git push -d origin feature"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-003") findings) "NFP-003"
          }
          test "detects empty-source deletion" {
              let findings = analyzeCommand "git push origin :refs/heads/feature"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-004") findings) "NFP-004"
          }
          test "detects mirror option" {
              let findings = analyzeCommand "git push --mirror origin"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-005") findings) "NFP-005"
          }
          test "detects prune option" {
              let findings = analyzeCommand "git push --prune origin"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-005") findings) "NFP-005"
          }
          test "detects no-verify" {
              let findings = analyzeCommand "git push --no-verify origin main"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-006") findings) "NFP-006"
          }
          test "detects send-pack" {
              let findings = analyzeCommand "git send-pack --force origin"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-008") findings) "NFP-008"
          }
          test "detects dynamic arguments" {
              let findings = analyzeCommand """git push "$@" """
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-007") findings) "NFP-007"
          }
          test "detects eval indirection" {
              let findings = analyzeCommand """eval "git push $args" """
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-007") findings) "NFP-007"
          }
          test "detects GitHub API force" {
              let findings = analyzeCommand "gh api repos/o/r/branches/b/protection --field force=true"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-009") findings) "NFP-009"
          }
          test "detects GitHub API ref deletion" {
              let findings = analyzeCommand "gh api repos/o/r/git/refs/heads/b --method DELETE"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-010") findings) "NFP-010"
          }
          test "detects curl DELETE ref" {
              let findings = analyzeCommand "curl -X DELETE https://api.github.com/repos/o/r/git/refs/heads/b"
              Expect.isTrue (List.exists (fun f -> f.RuleId = "NFP-010") findings) "NFP-010"
          }
          test "allows ordinary push" {
              let findings = analyzeCommand "git push origin main"
              Expect.isEmpty findings "no violations"
          }
          test "allows atomic push" {
              let findings = analyzeCommand "git push --atomic origin main"
              Expect.isEmpty findings "no violations"
          }
          test "allows follow-tags push" {
              let findings = analyzeCommand "git push --follow-tags origin main"
              Expect.isEmpty findings "no violations"
          }
          test "allows fetch" {
              let findings = analyzeCommand "git fetch origin"
              Expect.isEmpty findings "no violations"
          }
          test "handles adjacent quotes" {
              let findings = analyzeCommand "git push --for\"ce origin main"
              Expect.isNonEmpty findings "has findings"
          }
          test "handles split line continuation" {
              let findings = analyzeCommand "git push \\\n--force origin main"
              Expect.isNonEmpty findings "has findings"
          } ]
