module Circus.Tooling.Tests.SourcePolicy.BaselineTests

open Expecto
open Circus.Tooling.SourcePolicy.Baseline

let private writeAndLoad (content: string) =
    let tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "baseline-" + System.Guid.NewGuid().ToString("n") + ".csv")
    System.IO.File.WriteAllText(tmp, content.Replace("\n", System.Environment.NewLine))
    let result = load tmp
    System.IO.File.Delete tmp
    result

let tests =
    testList "Baseline CSV parsing" [
        test "valid baseline with two rows loads" {
            let csv = "path,violation_kind,physical_lines,sha256,owner,successor_act,reason\na.sh,oversized_shell,75,0000000000000000000000000000000000000000000000000000000000000000,owner,ACT,r\nb.sh,oversized_shell,80,1111111111111111111111111111111111111111111111111111111111111111,owner,ACT,r\n"
            match writeAndLoad csv with
            | Loaded entries -> Expect.equal (List.length entries) 2 "two rows"
            | Malformed msg -> failtestf "Expected Loaded, got Malformed %s" msg
            | Missing -> failtestf "Expected Loaded, got Missing"
        }
        test "missing file returns Missing" {
            match load "/nonexistent/circus/baseline.csv" with
            | Missing -> ()
            | _ -> failtestf "Expected Missing"
        }
        test "wrong header is Malformed" {
            let csv = "wrong,header\n"
            match writeAndLoad csv with
            | Malformed _ -> ()
            | _ -> failtestf "Expected Malformed"
        }
        test "forbidden baseline kind is Malformed" {
            let csv = "path,violation_kind,physical_lines,sha256,owner,successor_act,reason\nx.py,forbidden_source,1,0000000000000000000000000000000000000000000000000000000000000000,o,ACT,r\n"
            match writeAndLoad csv with
            | Malformed _ -> ()
            | _ -> failtestf "Expected Malformed"
        }
        test "uppercase SHA is Malformed" {
            let csv = "path,violation_kind,physical_lines,sha256,owner,successor_act,reason\nx.sh,oversized_shell,51,0000000000000000000000000000000000000000000000000000000000000000,o,ACT,r\n"
            match writeAndLoad csv with
            | Malformed _ -> ()
            | _ -> failtestf "Expected Malformed"
        }
        test "non-64-char SHA is Malformed" {
            let csv = "path,violation_kind,physical_lines,sha256,owner,successor_act,reason\nx.sh,oversized_shell,51,short,o,ACT,r\n"
            match writeAndLoad csv with
            | Malformed _ -> ()
            | _ -> failtestf "Expected Malformed"
        }
    ]
