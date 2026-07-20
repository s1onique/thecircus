module Circus.Tooling.Tests.SourcePolicy.ParityTests

/// Strict validator tests for ``factory/container-policy-parity.csv``.

open System
open System.IO
open Expecto

open Circus.Tooling.SourcePolicy.Parity
open Circus.Tooling.SourcePolicy.ContainerPolicy

let private newTempDir () : string =
    let path = Path.Combine(Path.GetTempPath(), "circus-parity-" + Guid.NewGuid().ToString("n"))
    Directory.CreateDirectory path |> ignore
    path

let private writeCsv (path: string) (rows: (string list) list) =
    let header = String.concat "," RequiredHeader
    let body =
        rows
        |> List.map (fun cols -> String.concat "," cols)
        |> String.concat "\n"
    File.WriteAllText(path, header + "\n" + body + "\n")

[<Tests>]
let tests =
    testList "Parity CSV validator" [
        test "committed CSV parses" {
            let path = Path.Combine("factory", "container-policy-parity.csv")
            Expect.isTrue (File.Exists path) "parity CSV must exist on the working tree"
            match parse path with
            | Result.Ok rows -> Expect.isGreaterThan (List.length rows) 0 "rows present"
            | Result.Error e -> failtestf "parse failed: %s" e
        }

        test "committed CSV validates identity equality with the rule registry" {
            let path = Path.Combine("factory", "container-policy-parity.csv")
            match validateFile path with
            | Ok r ->
                Expect.equal (List.length r.MissingIdentities) 0 "no missing identities"
                Expect.equal (List.length r.UnexpectedIdentities) 0 "no unexpected identities"
                Expect.equal (List.length r.DuplicateIdentities) 0 "no duplicate identities"
            | Failed (r, reasons) ->
                failtestf "parity validate failed: %s" (String.concat "; " reasons)
        }

        test "valid committed fixture (positive case)" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [ for id in CheckIds -> [ id; "desc"; id; "tools/.../ContainerPolicy.fs (fn)"; "positive"; "negative"; "complete" ] ]
            writeCsv path rows
            match validateFile path with
            | Ok r -> Expect.equal (List.length r.Rows) (List.length CheckIds) "all identities"
            | Failed (_, reasons) -> failtestf "should pass: %s" (String.concat "; " reasons)
            Directory.Delete(dir, true)
        }

        test "missing identity rejected" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows =
                CheckIds
                |> List.filter (fun id -> id <> "CP-01_required_files")
                |> List.map (fun id -> [ id; "desc"; id; "tools/.../ContainerPolicy.fs (fn)"; "positive"; "negative"; "complete" ])
            writeCsv path rows
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
                |> List.map (fun id -> [ id; "desc"; id; "tools/.../ContainerPolicy.fs (fn)"; "positive"; "negative"; "complete" ])
            writeCsv path rows
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
                (CheckIds @ [ "CP-01" ])
                |> List.map (fun id -> [ id; "desc"; id; "tools/.../ContainerPolicy.fs (fn)"; "positive"; "negative"; "complete" ])
            writeCsv path rows
            match validateFile path with
            | Failed (r, _) ->
                Expect.isGreaterThan (List.length r.DuplicateIdentities) 0 "duplicates flagged"
            | Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "invalid status rejected at parse" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [ for id in CheckIds -> [ id; "desc"; id; "tools/.../ContainerPolicy.fs (fn)"; "positive"; "negative"; "bogus" ] ]
            writeCsv path rows
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
            let header = String.concat "," (RequiredHeader @ [ "extra_column" ])
            File.WriteAllText(path, header + "\nCP-01,b,CP-01,loc,p,n,complete\n")
            match parse path with
            | Result.Error e -> Expect.stringContains e "extra header columns" "extra column flagged"
            | Result.Ok _ -> failtestf "should have failed"
            Directory.Delete(dir, true)
        }

        test "renderSummary emits a stable, single-line summary" {
            let dir = newTempDir ()
            let path = Path.Combine(dir, "parity.csv")
            let rows = [ for id in CheckIds -> [ id; "desc"; id; "tools/.../ContainerPolicy.fs (fn)"; "positive"; "negative"; "complete" ] ]
            writeCsv path rows
            let summary = renderSummary (validateFile path)
            Expect.stringContains summary "parity: PASS" "parity: PASS"
            Expect.stringContains summary "identities=" "identities field"
            Directory.Delete(dir, true)
        }
    ]