module Circus.Tooling.Tests.SourcePolicy.CliTests

open Expecto
open Circus.Tooling.SourcePolicy.Cli

let tests =
    testList "Cli parsing" [
        test "no args returns Help" {
            match parse [] with
            | Ok HelpCmd -> ()
            | _ -> failtestf "Expected Help"
        }
        test "help returns HelpCmd" {
            match parse ["help"] with
            | Ok HelpCmd -> ()
            | _ -> failtestf "Expected Help"
        }
        test "verify returns VerifyCmd" {
            match parse ["verify"] with
            | Ok (VerifyCmd _) -> ()
            | _ -> failtestf "Expected Verify"
        }
        test "verify --format json" {
            match parse ["verify"; "--format"; "json"] with
            | Ok (VerifyCmd "json") -> ()
            | _ -> failtestf "Expected Verify json"
        }
        test "explain requires path" {
            match parse ["explain"; "scripts/foo.sh"] with
            | Ok (ExplainCmd _) -> ()
            | _ -> failtestf "Expected Explain"
        }
        test "unknown format value is error" {
            match parse ["verify"; "--format"; "yaml"] with
            | Error _ -> ()
            | _ -> failtestf "Expected error"
        }
        test "unknown argument is error" {
            match parse ["verify"; "--bogus"] with
            | Error _ -> ()
            | _ -> failtestf "Expected error"
        }
    ]
