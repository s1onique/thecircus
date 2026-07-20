module Circus.Tooling.Tests.SourcePolicy.GateSummaryVerifyTests

/// Tests for ``GateSummaryVerify``.  These exercise the consumer-side
/// validator with both passing and intentionally-malformed inputs.

open System.IO
open Expecto

open Circus.Tooling.SourcePolicy.GateSummaryVerify

let private writeTempJson (content: string) : string =
    let path = Path.Combine(Path.GetTempPath(),
        "circus-gate-summary-verify-" + System.Guid.NewGuid().ToString("n") + ".json")
    File.WriteAllText(path, content)
    path

let private passingDoc () : string =
    """{
  "schema_version": 1,
  "generated_at": "2026-07-20T04:20:56Z",
  "tool": "circus-regenerate-gate-summary",
  "overall_status": "pass",
  "checks_total": 3,
  "checks_passed": 3,
  "checks_failed": 0,
  "checks_skipped": 0,
  "checks_unavailable": 0,
  "checks": [
    {"name": "container-publication-policy", "status": "pass", "exit_code": 0, "command": "x"},
    {"name": "executable-shell-tests", "status": "pass", "exit_code": 0, "command": "y"},
    {"name": "action-pin-mutation-test", "status": "pass", "exit_code": 0, "command": "z"}
  ],
  "tested_tree_oid": "0123456789abcdef0123456789abcdef01234567"
}"""

[<Tests>]
let tests =
    testList "GateSummaryVerify structural validator" [
        test "passing document validates with no failures" {
            let path = writeTempJson (passingDoc ())
            match validate path with
            | Ok r ->
                Expect.equal r.SchemaVersion 1 "schema_version"
                Expect.equal r.OverallStatus "pass" "overall_status"
                Expect.equal r.ChecksTotal 3 "checks_total"
                Expect.equal r.ChecksPassed 3 "checks_passed"
                Expect.equal r.ChecksFailed 0 "checks_failed"
                Expect.equal (List.length r.Checks) 3 "checks length"
                File.Delete path
            | Error msg ->
                File.Delete path
                failtestf "Expected Ok, got Error %s" msg
        }
        test "missing required field rejected" {
            let path = writeTempJson """{"schema_version": 1, "overall_status": "pass"}"""
            match validate path with
            | Error _ ->
                File.Delete path
                ()
            | Ok _ ->
                File.Delete path
                failtestf "Expected Error for missing fields"
        }
        test "schema_version != 1 rejected" {
            let bad = (passingDoc ()).Replace("\"schema_version\": 1", "\"schema_version\": 2")
            let path = writeTempJson bad
            match validate path with
            | Error msg ->
                File.Delete path
                Expect.isTrue (msg.Contains "schema_version") "mentions schema_version"
            | Ok _ ->
                File.Delete path
                failtestf "Expected Error for bad schema_version"
        }
        test "non-canonical overall_status rejected" {
            let bad = (passingDoc ()).Replace("\"overall_status\": \"pass\"", "\"overall_status\": \"green\"")
            let path = writeTempJson bad
            match validate path with
            | Error msg ->
                File.Delete path
                Expect.isTrue (msg.Contains "overall_status") "mentions overall_status"
            | Ok _ ->
                File.Delete path
                failtestf "Expected Error for non-canonical overall_status"
        }
        test "PascalCase twin field rejected (no case-insensitive fallback)" {
            let bad = (passingDoc ()).Replace("\"overall_status\"", "\"OverallStatus\"")
            let path = writeTempJson bad
            match validate path with
            | Error msg ->
                File.Delete path
                Expect.isTrue (msg.Contains "PascalCase") "explains PascalCase rejection"
            | Ok _ ->
                File.Delete path
                failtestf "Expected Error for PascalCase field"
        }
        test "invalid tested_tree_oid rejected" {
            let bad = (passingDoc ()).Replace("\"0123456789abcdef0123456789abcdef01234567\"", "\"not-a-sha\"")
            let path = writeTempJson bad
            match validate path with
            | Error msg ->
                File.Delete path
                Expect.isTrue (msg.Contains "tested_tree_oid") "mentions tested_tree_oid"
            | Ok _ ->
                File.Delete path
                failtestf "Expected Error for invalid tested_tree_oid"
        }
        test "count mismatch rejected" {
            let bad = (passingDoc ()).Replace("\"checks_passed\": 3", "\"checks_passed\": 2")
            let path = writeTempJson bad
            match validate path with
            | Error msg ->
                File.Delete path
                Expect.isTrue (msg.Contains "count inconsistency" || msg.Contains "does not match") "explains count mismatch"
            | Ok _ ->
                File.Delete path
                failtestf "Expected Error for count mismatch"
        }
        test "non-canonical per-check status rejected" {
            let bad = (passingDoc ()).Replace("\"status\": \"pass\"", "\"status\": \"passed\"")
            let path = writeTempJson bad
            match validate path with
            | Error _ ->
                File.Delete path
                ()
            | Ok _ ->
                File.Delete path
                failtestf "Expected Error for non-canonical check status"
        }
        test "malformed JSON rejected" {
            let path = writeTempJson "{ not valid json"
            match validate path with
            | Error _ ->
                File.Delete path
                ()
            | Ok _ ->
                File.Delete path
                failtestf "Expected Error for malformed JSON"
        }
        test "runVerify returns 0 for a passing document" {
            let path = writeTempJson (passingDoc ())
            let rc = runVerify path None
            File.Delete path
            Expect.equal rc 0 "exit 0"
        }
    ]
