module Circus.Tooling.Tests.NoForcePush.GitHubRulesTests

open System
open System.IO
open Expecto
open Circus.Tooling.NoForcePush.GitHubRules
open Circus.Tooling.NoForcePush.Types

[<Tests>]
let tests =
    testList
        "NoForcePush GitHubRules"
        [ test "computes evidence hash deterministically" {
              let hash1 = computeEvidenceHash """{"allow_force_pushes":{"enabled":false}}"""
              let hash2 = computeEvidenceHash """{"allow_force_pushes":{"enabled":false}}"""
              Expect.equal hash1 hash2 "deterministic"
              Expect.equal (hash1.Length) 64 "sha256 length"
          }
          test "different JSON produces different hash" {
              let hash1 = computeEvidenceHash """{"allow_force_pushes":{"enabled":false}}"""
              let hash2 = computeEvidenceHash """{"allow_force_pushes":{"enabled":true}}"""
              Expect.notEqual hash1 hash2 "different inputs"
          }
          test "parseGitHubResponse handles compliant rules" {
              let json = """
{
  "required_status_checks": { "strict": true, "contexts": [] },
  "enforce_admins": { "enabled": true },
  "required_pull_request_reviews": { "dismiss_stale_reviews": true },
  "restrictions": { "users": [], "teams": [], "apps": [] },
  "allow_force_pushes": { "enabled": false },
  "allow_deletions": { "enabled": false }
}
"""
              match parseGitHubResponse json "owner/repo" "main" with
              | Ok rule ->
                  Expect.isTrue rule.EnforcementActive "enforcement active"
                  Expect.isTrue rule.BlocksNonFastForward "blocks NFF"
                  Expect.isTrue rule.BlocksDeletion "blocks deletion"
                  Expect.isEmpty rule.BypassActors "no bypass actors"
              | Error e -> failwithf "parse error: %A" e
          }
          test "parseGitHubResponse detects missing force push block" {
              let json = """
{
  "required_status_checks": { "strict": true, "contexts": [] },
  "enforce_admins": { "enabled": true },
  "required_pull_request_reviews": { "dismiss_stale_reviews": true },
  "restrictions": { "users": [], "teams": [], "apps": [] },
  "allow_force_pushes": { "enabled": true },
  "allow_deletions": { "enabled": false }
}
"""
              match parseGitHubResponse json "owner/repo" "main" with
              | Ok rule ->
                  Expect.isFalse rule.BlocksNonFastForward "allows force push"
              | Error e -> failwithf "parse error: %A" e
          }
          test "parseGitHubResponse detects missing deletion block" {
              let json = """
{
  "required_status_checks": { "strict": true, "contexts": [] },
  "enforce_admins": { "enabled": true },
  "required_pull_request_reviews": { "dismiss_stale_reviews": true },
  "restrictions": { "users": [], "teams": [], "apps": [] },
  "allow_force_pushes": { "enabled": false },
  "allow_deletions": { "enabled": true }
}
"""
              match parseGitHubResponse json "owner/repo" "main" with
              | Ok rule ->
                  Expect.isFalse rule.BlocksDeletion "allows deletion"
              | Error e -> failwithf "parse error: %A" e
          }
          test "parseGitHubResponse handles bypass actors" {
              let json = """
{
  "required_status_checks": { "strict": true, "contexts": [] },
  "enforce_admins": { "enabled": true },
  "required_pull_request_reviews": { "dismiss_stale_reviews": true },
  "restrictions": { "users": ["admin1", "user1"], "teams": ["admins"], "apps": [] },
  "allow_force_pushes": { "enabled": false },
  "allow_deletions": { "enabled": false }
}
"""
              match parseGitHubResponse json "owner/repo" "main" with
              | Ok rule ->
                  Expect.equal (List.length rule.BypassActors) 3 "3 bypass actors"
              | Error e -> failwithf "parse error: %A" e
          }
          test "parseGitHubResponse handles null restrictions" {
              let json = """
{
  "required_status_checks": { "strict": true, "contexts": [] },
  "enforce_admins": { "enabled": true },
  "allow_force_pushes": { "enabled": false },
  "allow_deletions": { "enabled": false }
}
"""
              match parseGitHubResponse json "owner/repo" "main" with
              | Ok rule ->
                  Expect.isEmpty rule.BypassActors "no bypass actors when null"
              | Error e -> failwithf "parse error: %A" e
          }
          test "parseGitHubResponse handles missing optional fields" {
              let json = """
{
  "allow_force_pushes": { "enabled": false },
  "allow_deletions": { "enabled": false }
}
"""
              match parseGitHubResponse json "owner/repo" "main" with
              | Ok rule ->
                  Expect.isFalse rule.EnforcementActive "enforcement defaults to false"
                  Expect.isTrue rule.BlocksNonFastForward "blocks NFF"
                  Expect.isTrue rule.BlocksDeletion "blocks deletion"
              | Error e -> failwithf "parse error: %A" e
          }
          test "parseGitHubResponse fails on malformed JSON" {
              let json = """{ invalid json """
              match parseGitHubResponse json "owner/repo" "main" with
              | Ok _ -> failwith "should fail"
              | Error (MalformedJson _) -> ()
              | Error e -> failwithf "wrong error: %A" e
          } ]
