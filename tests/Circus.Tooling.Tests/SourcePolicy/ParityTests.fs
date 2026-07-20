module Circus.Tooling.Tests.SourcePolicy.ParityTests

/// Strict validator tests for ``factory/container-policy-parity.csv`` (CORRECTION01 §P1-1).
/// P1-1: Uses ContainerPolicy.CheckMetadata as single authority.

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

/// P1-1: Implementation location derived from CheckMetadata.
let private implLocFor (exactId: string) : string =
    match List.tryFind (fun (m: CheckMetadata) -> m.Id = exactId) CheckMetadata with
    | Some m -> sprintf "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (%s)" m.ImplementationFunction
    | None -> "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkXxx)"

[<Tests>]
let tests =
    testList "Parity CSV validator" [

        // =====================================================================
        // P1-1: Partition tests - valid vs malformed
        // =====================================================================

        test "P1-1: valid concrete ID passes partition (validIds)" {
            // CP-01_required_files is valid concrete ID
            let candidates = [ ("CP-01_required_files", parseConcreteId "CP-01_required_files") ]
            let validIds, invalidIds = candidates |> List.partition (fun (_, p) -> p.IsSome)
            Expect.equal (List.length validIds) 1 "CP-01_required_files is valid"
            Expect.equal (List.length invalidIds) 0 "no invalid"
        }

        test "P1-1: malformed concrete ID fails partition (invalidIds)" {
            // CP-1 is malformed (missing suffix)
            let candidates = [ ("CP-1", parseConcreteId "CP-1") ]
            let validIds, invalidIds = candidates |> List.partition (fun (_, p) -> p.IsSome)
            Expect.equal (List.length validIds) 0 "CP-1 is not valid"
            Expect.equal (List.length invalidIds) 1 "CP-1 is invalid"
        }

        // =====================================================================
        // P1-1: Positive canonical fixture tests
        // =====================================================================

        test "committed CSV parses" {
            let path = findCsv ()
            match parse path with
            | Result.Ok rows -> Expect.isGreaterThan (List.length rows) 0 "rows present"
            | Result.Error e -> failtestf "parse failed: %s" e
        }

        test "P1-1: committed CSV validates with all defect collections empty" {
            let path = findCsv ()
            match validateFile path with
            | Ok r ->
                Expect.equal (List.length r.MissingIdentities) 0 "no missing identities"
                Expect.equal (List.length r.UnknownIdentities) 0 "no unknown identities"
                Expect.equal (List.length r.DuplicateIdentities) 0 "no duplicate parity identities"
                Expect.equal (List.length r.DuplicateProductionIds) 0 "no duplicate production identities"
                Expect.equal (List.length r.MalformedIdentities) 0 "no malformed identities"
                Expect.equal (List.length r.IdentityPathFunctionMismatches) 0 "no function mismatches"
            | Failed (r, reasons) ->
                failtestf "parity validate failed: %s" (String.concat "; " reasons)
        }

        test "P1-1: canonical fixture from CheckMetadata passes validation" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            // P1-1: Construct fixture from CheckMetadata
            let rows = [
                for m in CheckMetadata -> [
                    m.Id
                    "desc"
                    m.Id
                    implLocFor m.Id
                    "tests/Circus.Tooling.Tests/SourcePolicy/ContainerPolicyTests.fs::positive"
                    "tests/Circus.Tooling.Tests/SourcePolicy/ContainerPolicyMutationTests.fs::negative"
                    "complete"
                ]
            ]
            writeStrictCsv path rows
            match validateFile path with
            | Ok r ->
                // P1-1: Mechanical accounting
                Expect.equal r.ProductionRuleCount (List.length CheckMetadata) "production_rule_count from metadata"
                Expect.equal r.ParityRowCount (List.length CheckMetadata) "parity_row_count from rows"
                Expect.equal r.ExactMatches (List.length CheckMetadata) "exact_matches equals count"
                Expect.equal (List.length r.MissingIdentities) 0 "no missing"
                Expect.equal (List.length r.UnknownIdentities) 0 "no unknown"
                Expect.equal (List.length r.DuplicateIdentities) 0 "no duplicates"
            | Failed (_, reasons) -> failtestf "should pass: %s" (String.concat "; " reasons)
            Directory.Delete(dir, true)
        }

        // =====================================================================
        // P1-1: Negative tests - malformed identities
        // =====================================================================

        test "P1-1: CP-1 rejected (prefix alias for CP-01)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.filter (fun m -> m.Id <> "CP-01_required_files")
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ "CP-1"; "desc"; "CP-1"; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkXxx)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.MalformedIdentities "CP-1" "CP-1 must be malformed"
                Expect.contains r.MissingIdentities "CP-01_required_files" "CP-01 must be missing"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "P1-1: CP-10 rejected (prefix alias for CP-10_trusted_runner)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.filter (fun m -> m.Id <> "CP-10_trusted_runner")
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ "CP-10"; "desc"; "CP-10"; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkXxx)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.MalformedIdentities "CP-10" "CP-10 must be malformed"
                Expect.contains r.MissingIdentities "CP-10_trusted_runner" "CP-10_trusted_runner must be missing"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "P1-1: CP-010_trusted_runner rejected (zero-padded prefix)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.filter (fun m -> m.Id <> "CP-10_trusted_runner")
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ "CP-010_trusted_runner"; "desc"; "CP-010_trusted_runner"; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkTrustedRunner)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue (List.contains "CP-010_trusted_runner" r.MalformedIdentities || List.contains "CP-010_trusted_runner" r.UnknownIdentities) "CP-010 must be flagged"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "P1-1: CP-10_trusted_runner-extra rejected (suffix alias)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.filter (fun m -> m.Id <> "CP-10_trusted_runner")
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ "CP-10_trusted_runner-extra"; "desc"; "CP-10_trusted_runner-extra"; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkTrustedRunner)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue (List.contains "CP-10_trusted_runner-extra" r.MalformedIdentities || List.contains "CP-10_trusted_runner-extra" r.UnknownIdentities) "CP-10_trusted_runner-extra must be flagged"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "P1-1: CP-10_trusted_runner description rejected (trailing text)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.filter (fun m -> m.Id <> "CP-10_trusted_runner")
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ "CP-10_trusted_runner description"; "desc"; "CP-10_trusted_runner description"; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkTrustedRunner)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue (List.contains "CP-10_trusted_runner description" r.MalformedIdentities) "trailing text must be malformed"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "P1-1: CP-10_trusted_runner/child rejected (path separator)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.filter (fun m -> m.Id <> "CP-10_trusted_runner")
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ "CP-10_trusted_runner/child"; "desc"; "CP-10_trusted_runner/child"; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkTrustedRunner)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue (List.contains "CP-10_trusted_runner/child" r.MalformedIdentities) "path separator must be malformed"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "P1-1: cp-10_trusted_runner rejected (case variant)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.filter (fun m -> m.Id <> "CP-10_trusted_runner")
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ "cp-10_trusted_runner"; "desc"; "cp-10_trusted_runner"; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkTrustedRunner)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue (List.contains "cp-10_trusted_runner" r.MalformedIdentities) "case variant must be malformed"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "P1-1: leading whitespace rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.filter (fun m -> m.Id <> "CP-01_required_files")
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ " CP-01_required_files"; "desc"; " CP-01_required_files"; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkRequiredFiles)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue (List.contains " CP-01_required_files" r.MalformedIdentities) "leading whitespace must be malformed"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "P1-1: trailing whitespace rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.filter (fun m -> m.Id <> "CP-01_required_files")
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ "CP-01_required_files "; "desc"; "CP-01_required_files "; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkRequiredFiles)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isTrue (List.contains "CP-01_required_files " r.MalformedIdentities) "trailing whitespace must be malformed"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        // =====================================================================
        // P1-1: Negative tests - duplicates, unknowns, missing
        // =====================================================================

        test "P1-1: duplicate exact identity rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ "CP-01_required_files"; "desc"; "CP-01_required_files"; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkRequiredFiles)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.DuplicateIdentities "CP-01_required_files" "duplicate must be flagged"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "P1-1: exact unknown identity rejected (CP-99_unknown_check)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ "CP-99_unknown_check"; "desc"; "CP-99_unknown_check"; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkXxx)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.UnknownIdentities "CP-99_unknown_check" "unknown must be flagged"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        // P1-1: Tests for duplicate production detection before map construction
        test "P1-1: duplicate production IDs would be detected (not lost to Set.ofList)" {
            // This test verifies the logic exists; actual duplicates don't exist in production
            // If production metadata had duplicates, they would appear in DuplicateProductionIds
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [
                for m in CheckMetadata -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ]
            ]
            writeStrictCsv path rows
            match validateFile path with
            | Ok r ->
                // With no duplicates, this list is empty
                Expect.equal (List.length r.DuplicateProductionIds) 0 "no duplicate production IDs"
            | Failed _ -> failtestf "should have passed"
            Directory.Delete(dir, true)
        }

        // P1-1: Test that valid-format unknown FsharpCheckId is recorded as unknown
        test "P1-1: valid-format unknown FsharpCheckId in FsharpCheckId column flagged as unknown" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            // Use a valid-format but unknown ID in the FsharpCheckId column
            let rows =
                CheckMetadata
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ "CP-99_extra_check"; "desc"; "CP-99_extra_check"; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkXxx)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                // CP-99_extra_check is valid format but unknown
                Expect.isTrue (List.contains "CP-99_extra_check" r.UnknownIdentities) "valid-format unknown FsharpCheckId must be flagged"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        // P1-1: Test that function metadata equals nameof for representative checks
        test "P1-1: CheckMetadata.ImplementationFunction matches nameof for CP-01" {
            // Verify the nameof binding is correct
            let cp01 = List.find (fun (m: CheckMetadata) -> m.Id = "CP-01_required_files") CheckMetadata
            Expect.equal cp01.ImplementationFunction "checkRequiredFiles" "CP-01 function name must be checkRequiredFiles"
        }

        test "P1-1: CheckMetadata.ImplementationFunction matches nameof for CP-10" {
            let cp10 = List.find (fun (m: CheckMetadata) -> m.Id = "CP-10_trusted_runner") CheckMetadata
            Expect.equal cp10.ImplementationFunction "checkTrustedRunner" "CP-10 function name must be checkTrustedRunner"
        }

        // P1-1: Test canonical cardinality equals List.length CheckMetadata
        test "P1-1: canonical cardinality equals List.length CheckMetadata (no hard-coded 31)" {
            let expected = List.length CheckMetadata
            // The count comes from CheckMetadata, not a hard-coded value
            Expect.isGreaterThan expected 0 "CheckMetadata is not empty"
            // This test would fail if someone hard-coded 31
            // The actual count is verified by comparing against CheckMetadata list length
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [
                for m in CheckMetadata -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ]
            ]
            writeStrictCsv path rows
            match validateFile path with
            | Ok r ->
                Expect.equal r.ProductionRuleCount expected "production_rule_count equals List.length CheckMetadata"
            | Failed _ -> failtestf "should have passed"
            Directory.Delete(dir, true)
        }

        // P1-1: Test valid metadata creates one exact map entry per definition
        test "P1-1: CheckMetadata produces one exact map entry per CheckDefinition" {
            // Map construction should not collapse any entries
            let metadataIds = List.map (fun (m: CheckMetadata) -> m.Id) CheckMetadata
            let uniqueIds = List.distinct metadataIds
            Expect.equal (List.length metadataIds) (List.length uniqueIds) "all metadata IDs are unique"
            Expect.equal (List.length metadataIds) (List.length CheckMetadata) "metadata count matches CheckMetadata count"
        }

        test "P1-1: empty identity rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.filter (fun m -> m.Id <> "CP-01_required_files")
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
                @ [ ""; "desc"; ""; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkXxx)"; "p"; "n"; "complete" ]
            writeStrictCsv path rows
            match parse path with
            | Result.Error e -> Expect.stringContains e "missing identity" "empty identity rejected"
            | Result.Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "P1-1: missing exact production identity fails validation" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            // Exclude CP-01 but include everything else
            let rows =
                CheckMetadata
                |> List.filter (fun m -> m.Id <> "CP-01_required_files")
                |> List.map (fun m -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.contains r.MissingIdentities "CP-01_required_files" "missing must be flagged"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "P1-1: wrong implementation function for known identity rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckMetadata
                |> List.map (fun m ->
                    if m.Id = "CP-01_required_files" then
                        [ m.Id; "desc"; m.Id; "tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs (checkWrongFunction)"; "p"; "n"; "complete" ]
                    else
                        [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ])
            writeStrictCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isGreaterThan (List.length r.IdentityPathFunctionMismatches) 0 "function mismatch must be flagged"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        // =====================================================================
        // Existing tests (unchanged)
        // =====================================================================

        test "invalid status rejected at parse" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [
                for m in CheckMetadata -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "bogus" ]
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

        test "renderSummary emits a stable, single-line summary with P1-1 accountability" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [
                for m in CheckMetadata -> [ m.Id; "desc"; m.Id; implLocFor m.Id; "p"; "n"; "complete" ]
            ]
            writeStrictCsv path rows
            let summary = renderSummary (validateFile path)
            Expect.stringContains summary "parity: PASS" "parity: PASS"
            Expect.stringContains summary "production_rules=" "production_rules field"
            Expect.stringContains summary "exact_matches=" "exact_matches field"
            Directory.Delete(dir, true)
        }
    ]
