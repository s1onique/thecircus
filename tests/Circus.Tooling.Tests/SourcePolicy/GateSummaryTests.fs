module Circus.Tooling.Tests.SourcePolicy.GateSummaryTests

/// Focused tests for the canonical Leamas v1 wire contract emitted by
/// ``Circus.Tooling.SourcePolicy.GateSummary``.

open System.IO
open Expecto

open Circus.Tooling.SourcePolicy.GateSummary

let private sampleDoc () : GateSummaryDoc =
    {
        SchemaVersion = 1
        GeneratedAt = "2026-07-20T04:20:56Z"
        Tool = "circus-regenerate-gate-summary"
        OverallStatus = "pass"
        ChecksTotal = 3
        ChecksPassed = 3
        ChecksFailed = 0
        ViolationsTotal = 0
        ChecksSkipped = 0
        ChecksUnavailable = 0
        Checks =
            [
                { Name = "container-publication-policy"
                  Status = "pass"
                  ExitCode = 0
                  Command = "dotnet circus-tooling.dll container-policy verify" }
                { Name = "executable-shell-tests"
                  Status = "pass"
                  ExitCode = 0
                  Command = "bash tests/ci/test_build_publish_shell.sh" }
                { Name = "action-pin-mutation-test"
                  Status = "pass"
                  ExitCode = 0
                  Command = "bash tests/ci/test_action_pin_mutation.sh" }
            ]
        TestedCommitOid = "0123456789abcdef0123456789abcdef01234567"
        TestedTreeOid = "fedcba9876543210fedcba9876543210fedcba98"
    }

let private findFirst (s: string) (needle: string) : int =
    let mutable idx = 0
    let mutable found = -1
    while idx <= s.Length - needle.Length && found < 0 do
        let mutable match_ = true
        for j in 0 .. needle.Length - 1 do
            if s.[idx + j] <> needle.[j] then match_ <- false
        if match_ then found <- idx
        idx <- idx + 1
    found

[<Tests>]
let tests =
    testList "GateSummary wire contract" [
        test "serialized document contains schema_version" {
            let json = serialize (sampleDoc ())
            Expect.isGreaterThan (findFirst json "schema_version") -1 "schema_version present"
        }
        test "serialized document does not contain SchemaVersion" {
            let json = serialize (sampleDoc ())
            Expect.equal (findFirst json "SchemaVersion") -1 "SchemaVersion absent"
        }
        test "serialized document contains every required top-level field" {
            let json = serialize (sampleDoc ())
            for field in [
                "schema_version"; "generated_at"; "tool"; "overall_status"
                "checks_total"; "checks_passed"; "checks_failed"
                "checks_skipped"; "checks_unavailable"
                "checks"; "tested_commit_oid"; "tested_tree_oid"
            ] do
                Expect.isGreaterThan (findFirst json field) -1 (sprintf "%s present" field)
        }
        test "serialized document contains every required per-check field" {
            let json = serialize (sampleDoc ())
            for field in [ "name"; "status"; "exit_code"; "command" ] do
                Expect.isGreaterThan (findFirst json field) -1 (sprintf "%s present" field)
        }
        test "top-level counts are integers" {
            let json = serialize (sampleDoc ())
            Expect.isGreaterThan (findFirst json "\"checks_total\": 3") -1 "checks_total integer literal"
            Expect.isGreaterThan (findFirst json "\"checks_passed\": 3") -1 "checks_passed integer literal"
        }
        test "command field is rendered as a single string not an array" {
            let json = serialize (sampleDoc ())
            Expect.isGreaterThan (findFirst json "\"command\":") -1 "command rendered as string property"
            Expect.equal (findFirst json "\"command\": [") -1 "command must not be rendered as an array"
        }
        test "statusFor maps zero exit to pass" {
            Expect.equal (statusForExitCode 0) "pass" "pass"
        }
        test "statusFor maps nonzero exit to fail" {
            Expect.equal (statusForExitCode 1) "fail" "fail"
            Expect.equal (statusForExitCode 127) "fail" "fail"
        }
        test "sample doc reflects canonical Leamas v1 vocabulary" {
            let json = serialize (sampleDoc ())
            Expect.isGreaterThan (findFirst json "\"pass\"") -1 "pass present"
        }
    ]
