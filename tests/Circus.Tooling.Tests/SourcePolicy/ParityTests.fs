module Circus.Tooling.Tests.SourcePolicy.ParityTests

/// Strict validator tests for ``factory/container-policy-parity.csv`` (CORRECTION01 §P1-1).

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

/// Mapping from ``CP-NN`` -> production function name.  Mirrors the
/// authoritative ``ruleFunctionName`` table inside ``Parity`` so the
/// fixtures can declare the correct function for each rule.
let private productionFunctionName : Map<string, string> =
    Map.ofList [
        "CP-01", "checkRequiredFiles"
        "CP-02", "checkShellExecutable"
        "CP-03", "checkDockerignore"
        "CP-04", "checkWorkflowTriggers"
        "CP-05", "checkPushBranchRestriction"
        "CP-06", "checkMinimalPermissions"
        "CP-07", "checkReferenceScopedConcurrency"
        "CP-08", "checkReusableInputs"
        "CP-09", "checkNoPullRequestTarget"
        "CP-10", "checkTrustedRunner"
        "CP-11", "checkHarborRepositoryNaming"
        "CP-12", "checkPasswordStdin"
        "CP-13", "checkTlsBypass"
        "CP-14", "checkPrivateCaAndBuildkit"
        "CP-15", "checkCacheSeparation"
        "CP-16", "checkPublishGating"
        "CP-17", "checkCacheImportExport"
        "CP-18", "checkImmutableTags"
        "CP-19", "checkLatestTagContract"
        "CP-20", "checkSecretMountCleanup"
        "CP-21", "checkElmInstaller"
        "CP-22", "checkNumericUsers"
        "CP-23", "checkPortContracts"
        "CP-24", "checkSmokeEndpoints"
        "CP-25", "checkDigestPullInspect"
        "CP-26", "checkWorkflowSeams"
        "CP-27", "checkGithubOutputContracts"
        "CP-28", "checkActionPins"
        "CP-29", "checkTrackedSecrets"
        "CP-30", "checkFinalStageExclusions"
        "CP-31", "checkGateSummaryAcceptance"
    ]

/// Build the implementation-location string the production code
/// would emit for the given ``CP-NN``.
let private implLocFor (cpId: string) : string =
    match Map.tryFind cpId productionFunctionName with
    | Some fn -> sprintf "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (%s)" fn
    | None -> "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkXxx)"

[<Tests>]
let tests =
    testList "Parity CSV validator" [
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
            | Failed (r, reasons) ->
                failtestf "parity validate failed: %s" (String.concat "; " reasons)
        }

        test "valid committed fixture (positive case)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [
                for id in CheckIds -> [
                    id
                    "desc"
                    id
                    implLocFor (id.Split('_').[0])
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

        test "missing identity rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.filter (fun id -> id <> "CP-01_required_files")
                |> List.map (fun id -> [ id; "desc"; id; implLocFor (id.Split('_').[0]); "p"; "n"; "complete" ])
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.MissingIdentities "CP-01" "CP-01 must be flagged missing"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "unexpected identity rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                (CheckIds @ [ "CP-99_does_not_exist" ])
                |> List.map (fun id -> [ id; "desc"; id; implLocFor (id.Split('_').[0]); "p"; "n"; "complete" ])
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.UnexpectedIdentities "CP-99" "CP-99 must be flagged unexpected"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "duplicate identity rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                (CheckIds @ [ "CP-01_required_files" ])
                |> List.map (fun id -> [ id; "desc"; id; implLocFor (id.Split('_').[0]); "p"; "n"; "complete" ])
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isGreaterThan (List.length r.DuplicateIdentities) 0 "duplicates flagged"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "invalid status rejected at parse" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [
                for id in CheckIds -> [ id; "desc"; id; implLocFor (id.Split('_').[0]); "p"; "n"; "bogus" ]
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
            File.WriteAllText(path, "legacy_check_id,legacy_behavior\nCP-01,b\n")
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

        test "renderSummary emits a stable, single-line summary" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [
                for id in CheckIds -> [ id; "desc"; id; implLocFor (id.Split('_').[0]); "p"; "n"; "complete" ]
            ]
            writeStrictCsv path rows
            let summary = renderSummary (validateFile path)
            Expect.stringContains summary "parity: PASS" "parity: PASS"
            Expect.stringContains summary "identities=" "identities field"
            Directory.Delete(dir, true)
        }
    ]
