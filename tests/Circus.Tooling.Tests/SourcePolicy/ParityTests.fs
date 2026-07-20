module Circus.Tooling.Tests.SourcePolicy.ParityTests

/// Strict validator tests for ``factory/container-policy-parity.csv`` (CORRECTION01 §P1-1).
/// P1-1 eliminates prefix aliasing: identities must match exactly using ContainerPolicy.CheckIds.

open System
open System.IO
open Expecto

open Circus.Tooling.SourcePolicy.Parity
open Circus.Tooling.SourcePolicy.ContainerPolicy

let private newTempDir () : string =
    let path = Path.Combine(Path.GetTempPath(), "circus-parity-" + Guid.NewGuid().ToString("n"))
    Directory.CreateDirectory path |> ignore
    path

let private writeStrictCsv (path: string) (rows: string list list) =
    let quote (s: string) = "\"" + s.Replace("\"", "\"\"") + "\""
    let renderRow (cols: string list) = cols |> List.map quote |> String.concat ","
    let header = renderRow RequiredHeader
    let body = rows |> List.map renderRow |> String.concat "\n"
    File.WriteAllText(path, header + "\n" + body + "\n")

let private findCsv () : string =
    let mutable d = Directory.GetCurrentDirectory()
    let mutable found : string option = None
    for _ in 0 .. 6 do
        let candidate = Path.Combine(d, "factory", "container-policy-parity.csv")
        if File.Exists candidate then
            found <- Some candidate
        let parent = Directory.GetParent d
        if isNull parent then ()
        else d <- parent.FullName
    match found with
    | Some p -> p
    | None -> Path.Combine("factory", "container-policy-parity.csv")

/// Mapping from exact ``CP-NN_suffix`` -> production function name.
/// P1-1: Uses exact identity keys from CheckIds.
let private productionFunctionName : Map<string, string> =
    Map.ofList [
        "CP-01_required_files", "checkRequiredFiles"
        "CP-02_shell_executable", "checkShellExecutable"
        "CP-03_dockerignore", "checkDockerignore"
        "CP-04_workflow_triggers", "checkWorkflowTriggers"
        "CP-05_push_main", "checkPushBranchRestriction"
        "CP-05_push_tags", "checkPushBranchRestriction"
        "CP-06_minimal_permissions", "checkMinimalPermissions"
        "CP-07_concurrency", "checkReferenceScopedConcurrency"
        "CP-08_reusable_inputs", "checkReusableInputs"
        "CP-08_reusable_push_type", "checkReusableInputs"
        "CP-09_no_pull_request_target", "checkNoPullRequestTarget"
        "CP-10_trusted_runner", "checkTrustedRunner"
        "CP-11_harbor_naming", "checkHarborRepositoryNaming"
        "CP-11_harbor_image_contract", "checkHarborRepositoryNaming"
        "CP-12_password_stdin", "checkPasswordStdin"
        "CP-13_tls_bypass", "checkTlsBypass"
        "CP-14_ca_secret", "checkPrivateCaAndBuildkit"
        "CP-14_buildkit_config", "checkPrivateCaAndBuildkit"
        "CP-14_buildkit_registry", "checkPrivateCaAndBuildkit"
        "CP-14_reusable_ca", "checkPrivateCaAndBuildkit"
        "CP-15_cache_template", "checkCacheSeparation"
        "CP-15_cache_image_specific", "checkCacheSeparation"
        "CP-15_cache_distinct", "checkCacheSeparation"
        "CP-16_build_publish_marker", "checkPublishGating"
        "CP-16_publish_publish_marker", "checkPublishGating"
        "CP-16_build_compare", "checkPublishGating"
        "CP-16_publish_compare", "checkPublishGating"
        "CP-16_reusable_publish_forward", "checkPublishGating"
        "CP-17_cache_from", "checkCacheImportExport"
        "CP-17_cache_to", "checkCacheImportExport"
        "CP-17_cache_mode_max", "checkCacheImportExport"
        "CP-17_cache_oci_manifest", "checkCacheImportExport"
        "CP-18_immutable_tag", "checkImmutableTags"
        "CP-18_release_tag", "checkImmutableTags"
        "CP-18_trusted_guard", "checkImmutableTags"
        "CP-19_latest_present", "checkLatestTagContract"
        "CP-19_latest_main_only", "checkLatestTagContract"
        "CP-19_latest_unique", "checkLatestTagContract"
        "CP-20_secret_marker", "checkSecretMountCleanup"
        "CP-20_update_ca", "checkSecretMountCleanup"
        "CP-20_legacy_path", "checkSecretMountCleanup"
        "CP-21_elm_marker", "checkElmInstaller"
        "CP-21_elm_version", "checkElmInstaller"
        "CP-22_backend_user", "checkNumericUsers"
        "CP-22_frontend_user", "checkNumericUsers"
        "CP-23_backend_port", "checkPortContracts"
        "CP-23_frontend_port", "checkPortContracts"
        "CP-24_backend_smoke", "checkSmokeEndpoints"
        "CP-24_frontend_smoke", "checkSmokeEndpoints"
        "CP-25_digest_pull", "checkDigestPullInspect"
        "CP-25_digest_inspect", "checkDigestPullInspect"
        "CP-25_amd64_verify", "checkDigestPullInspect"
        "CP-26_seam_step", "checkWorkflowSeams"
        "CP-26_seam_forward", "checkWorkflowSeams"
        "CP-27_github_output", "checkGithubOutputContracts"
        "CP-27_workflow_output", "checkGithubOutputContracts"
        "CP-28_action_pin", "checkActionPins"
        "CP-28_action_allowlist", "checkActionPins"
        "CP-28_action_sha_pin", "checkActionPins"
        "CP-28_action_present", "checkActionPins"
        "CP-29_tracked_secrets", "checkTrackedSecrets"
        "CP-30_final_stage_material", "checkFinalStageExclusions"
        "CP-31_acceptance_marker", "checkGateSummaryAcceptance"
        "CP-31_acceptance_vocab", "checkGateSummaryAcceptance"
        "CP-31_publish_branch_coverage", "checkGateSummaryAcceptance"
        "CP-31_wire_coverage", "checkGateSummaryAcceptance"
        "CP-31_github_output_assertion", "checkGateSummaryAcceptance"
    ]

/// Build the implementation-location string for the given exact identity.
/// P1-1: Uses exact identity lookup.
let private implLocFor (exactId: string) : string =
    match Map.tryFind exactId productionFunctionName with
    | Some fn -> sprintf "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (%s)" fn
    | None -> "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkXxx)"

[<Tests>]
let tests =
    testList "Parity CSV validator" [

        // =====================================================================
        // P1-1: Exact identity positive tests
        // =====================================================================

        test "committed CSV parses" {
            let path = findCsv ()
            match parse path with
            | Result.Ok rows -> Expect.isGreaterThan (List.length rows) 0 "rows present"
            | Result.Error e -> failtestf "parse failed: %s" e
        }

        test "committed CSV validates identity equality with the rule registry" {
            let path = findCsv ()
            match validateFile path with
            | Ok r ->
                Expect.equal (List.length r.MissingIdentities) 0 "no missing identities"
                Expect.equal (List.length r.UnexpectedIdentities) 0 "no unexpected identities"
                Expect.equal (List.length r.DuplicateIdentities) 0 "no duplicate identities"
                Expect.equal (List.length r.IdentityPathFunctionMismatches) 0 "no function mismatches"
                // P1-1: Additional exactness checks
                Expect.equal (List.length r.MalformedIdentities) 0 "no malformed identities"
                Expect.equal (List.length r.DuplicateProductionIds) 0 "no production duplicates"
            | Failed (r, reasons) ->
                failtestf "parity validate failed: %s" (String.concat "; " reasons)
        }

        test "valid committed fixture passes (positive case)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            // P1-1: Use exact CheckIds (full IDs with suffixes)
            let rows = [
                for id in CheckIds -> [
                    id
                    "desc"
                    id
                    implLocFor id
                    "tests/Circus.Tooling.Tests/SourcePolicy/ContainerPolicyTests.fs::positive"
                    "tests/Circus.Tooling.Tests/SourcePolicy/ContainerPolicyMutationTests.fs::negative"
                    "complete"
                ]
            ]
            writeStrictCsv path rows
            match validateFile path with
            | Ok _ -> ()
            | Failed (_, reasons) -> failtestf "should pass: %s" (String.concat "; " reasons)
            Directory.Delete(dir, true)
        }

        // =====================================================================
        // P1-1: Exact identity negative tests - prefix aliasing rejection
        // =====================================================================

        test "P1-1: CP-1 cannot alias CP-10 (prefix rejection)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            // Build fixture with exact CP-10 but missing CP-01
            let rows =
                CheckIds
                |> List.filter (fun id -> not (id.StartsWith("CP-01")))  // exclude CP-01
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                // CP-01 should be flagged as missing
                Expect.contains r.MissingIdentities "CP-01_required_files"
                    "CP-01_required_files must be flagged as missing"
            | Ok _ -> failtestf "should have failed due to missing CP-01"
            Directory.Delete(dir, true)
        }

        test "P1-1: CP-10-extra rejected (suffix aliasing)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            // Add a suffix-bearing identity that is NOT in CheckIds
            let rows =
                CheckIds
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
                @ [ "CP-10_trusted_runner-extra"; "desc"; "CP-10_trusted_runner-extra";
                    "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkTrustedRunner)";
                    "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                // Should flag CP-10_trusted_runner-extra as unexpected/malformed
                let isFlagged =
                    List.contains "CP-10_trusted_runner-extra" r.UnexpectedIdentities ||
                    List.contains "CP-10_trusted_runner-extra" r.MalformedIdentities
                Expect.isTrue isFlagged "suffix-bearing identity must be flagged"
            | Ok _ -> failtestf "should have failed due to suffix-bearing identity"
            Directory.Delete(dir, true)
        }

        test "P1-1: CP-10 description rejected (trailing text aliasing)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
                @ [ "CP-10_trusted_runner description"; "desc"; "CP-10_trusted_runner description";
                    "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkTrustedRunner)";
                    "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue
                    (List.contains "CP-10_trusted_runner description" r.MalformedIdentities ||
                     List.contains "CP-10_trusted_runner description" r.UnexpectedIdentities)
                    "trailing text identity must be flagged"
            | Ok _ -> failtestf "should have failed due to trailing text"
            Directory.Delete(dir, true)
        }

        test "P1-1: CP-10/child rejected (path separator aliasing)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
                @ [ "CP-10_trusted_runner/child"; "desc"; "CP-10_trusted_runner/child";
                    "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkTrustedRunner)";
                    "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue
                    (List.contains "CP-10_trusted_runner/child" r.MalformedIdentities ||
                     List.contains "CP-10_trusted_runner/child" r.UnexpectedIdentities)
                    "path separator identity must be flagged"
            | Ok _ -> failtestf "should have failed due to path separator"
            Directory.Delete(dir, true)
        }

        test "P1-1: cp-10 rejected (case variant aliasing)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.filter (fun id -> id <> "CP-10_trusted_runner")  // exclude exact CP-10
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
                @ [ "cp-10_trusted_runner"; "desc"; "cp-10_trusted_runner";
                    "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkTrustedRunner)";
                    "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue
                    (List.contains "cp-10_trusted_runner" r.MalformedIdentities ||
                     List.contains "cp-10_trusted_runner" r.UnexpectedIdentities)
                    "case variant identity must be flagged"
            | Ok _ -> failtestf "should have failed due to case variant"
            Directory.Delete(dir, true)
        }

        test "P1-1: leading whitespace rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.filter (fun id -> id <> "CP-01_required_files")
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
                @ [ " CP-01_required_files"; "desc"; " CP-01_required_files";
                    "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkRequiredFiles)";
                    "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue
                    (List.contains " CP-01_required_files" r.MalformedIdentities)
                    "leading whitespace identity must be flagged"
            | Ok _ -> failtestf "should have failed due to leading whitespace"
            Directory.Delete(dir, true)
        }

        test "P1-1: trailing whitespace rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.filter (fun id -> id <> "CP-01_required_files")
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
                @ [ "CP-01_required_files "; "desc"; "CP-01_required_files ";
                    "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkRequiredFiles)";
                    "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue
                    (List.contains "CP-01_required_files " r.MalformedIdentities)
                    "trailing whitespace identity must be flagged"
            | Ok _ -> failtestf "should have failed due to trailing whitespace"
            Directory.Delete(dir, true)
        }

        test "P1-1: duplicate CP-10 rows rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
                @ [ "CP-10_trusted_runner"; "desc"; "CP-10_trusted_runner";
                    "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkTrustedRunner)";
                    "p"; "n"; "complete" ]  // duplicate
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.DuplicateIdentities "CP-10_trusted_runner"
                    "duplicate identity must be flagged"
            | Ok _ -> failtestf "should have failed due to duplicate"
            Directory.Delete(dir, true)
        }

        test "P1-1: unknown CP-999 rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
                @ [ "CP-99_unknown_check"; "desc"; "CP-99_unknown_check";
                    "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkXxx)";
                    "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue
                    (List.contains "CP-99_unknown_check" r.UnexpectedIdentities ||
                     List.contains "CP-99_unknown_check" r.MalformedIdentities)
                    "unknown identity must be flagged"
            | Ok _ -> failtestf "should have failed due to unknown identity"
            Directory.Delete(dir, true)
        }

        test "P1-1: empty identifier rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.filter (fun id -> id <> "CP-01_required_files")
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
                @ [ ""; "desc"; ""; "loc"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match parse path with
            | Result.Error e ->
                Expect.stringContains e "missing identity" "empty identity should be rejected at parse"
            | Result.Ok _ -> failtestf "should have failed due to empty identity"
            Directory.Delete(dir, true)
        }

        test "P1-1: missing production rule fails validation" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            // Exclude CP-01 but include everything else
            let rows =
                CheckIds
                |> List.filter (fun id -> id <> "CP-01_required_files")
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.MissingIdentities "CP-01_required_files"
                    "missing production rule must be flagged"
            | Ok _ -> failtestf "should have failed due to missing rule"
            Directory.Delete(dir, true)
        }

        // =====================================================================
        // Existing tests (updated for exact identity)
        // =====================================================================

        test "missing identity rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.filter (fun id -> id <> "CP-01_required_files")
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.MissingIdentities "CP-01_required_files"
                    "CP-01_required_files must be flagged missing"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "unexpected identity rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                (CheckIds @ [ "CP-99_does_not_exist" ])
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.UnexpectedIdentities "CP-99_does_not_exist"
                    "CP-99_does_not_exist must be flagged unexpected"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "duplicate identity rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                (CheckIds @ [ "CP-01_required_files" ])  // exact duplicate
                |> List.map (fun id -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ])
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.DuplicateIdentities "CP-01_required_files"
                    "duplicate identity must be flagged"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "invalid status rejected at parse" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [
                for id in CheckIds -> [ id; "desc"; id; implLocFor id; "p"; "n"; "bogus" ]
            ]
            writeStrictCsv path rows
            match parse path with
            | Result.Error e -> Expect.stringContains e "invalid status" "status error"
            | Result.Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "missing header rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            File.WriteAllText(path, "legacy_check_id,legacy_behavior\nCP-01_required_files,b\n")
            match parse path with
            | Result.Error _ -> ()
            | Result.Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "extra forbidden column rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let headerQuoted =
                (RequiredHeader @ [ "extra_column" ])
                |> List.map (sprintf "\"%s\"")
                |> String.concat ","
            File.WriteAllText(path, headerQuoted + "\n\"a\",\"b\",\"a\",\"tools/.../ContainerPolicy.fs (checkXxx)\",\"p\",\"n\",\"complete\"\n")
            match parse path with
            | Result.Error e -> Expect.stringContains e "extra header columns" "extra column flagged"
            | Result.Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "reordered header columns rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let reordered = [
                "fsharp_check_id"; "legacy_check_id"; "legacy_behavior"
                "implementation_location"; "positive_test"; "negative_mutation_test"
                "status"
            ]
            let header = reordered |> List.map (sprintf "\"%s\"") |> String.concat ","
            File.WriteAllText(path, header + "\n\"a\",\"a\",\"b\",\"loc\",\"p\",\"n\",\"complete\"\n")
            match parse path with
            | Result.Error e -> Expect.stringContains e "header column order" "order mismatch flagged"
            | Result.Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "character after closing quote rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            File.WriteAllText(path, "\"a\"X,\"b\",\"c\"\n")
            match parse path with
            | Result.Error _ -> ()
            | Result.Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "wrong implementation function rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.map (fun id -> [ id; "desc"; id; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkXxxWrong)"; "p"; "n"; "complete" ])
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isGreaterThan (List.length r.IdentityPathFunctionMismatches) 0 "function mismatch flagged"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "renderSummary emits a stable, single-line summary with P1-1 accountability" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [
                for id in CheckIds -> [ id; "desc"; id; implLocFor id; "p"; "n"; "complete" ]
            ]
            writeStrictCsv path rows
            let summary = renderSummary (validateFile path)
            Expect.stringContains summary "parity: PASS" "parity: PASS"
            // P1-1: Check for new accountability fields
            Expect.stringContains summary "production_rules=" "production_rules field"
            Expect.stringContains summary "malformed=" "malformed field"
            Directory.Delete(dir, true)
        }
    ]
