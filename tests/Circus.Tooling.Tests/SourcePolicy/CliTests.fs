module Circus.Tooling.Tests.SourcePolicy.CliTests

open Expecto
open Circus.Tooling.SourcePolicy.Cli

[<Tests>]
let tests =
    testList
        "Cli parsing"
        [ test "no args returns Help" {
              match parse [] with
              | Ok HelpCmd -> ()
              | _ -> failtestf "Expected Help"
          }
          test "help returns HelpCmd" {
              match parse [ "help" ] with
              | Ok HelpCmd -> ()
              | _ -> failtestf "Expected Help"
          }
          test "verify returns VerifyCmd" {
              match parse [ "source-policy"; "verify" ] with
              | Ok(VerifyCmd _) -> ()
              | _ -> failtestf "Expected Verify"
          }
          test "verify --format json" {
              match parse [ "source-policy"; "verify"; "--format"; "json" ] with
              | Ok(VerifyCmd "json") -> ()
              | _ -> failtestf "Expected Verify json"
          }
          test "container-policy verify" {
              match parse [ "container-policy"; "verify" ] with
              | Ok(ContainerPolicyCmd _) -> ()
              | _ -> failtestf "Expected ContainerPolicy"
          }
          test "gate-summary regenerate" {
              match parse [ "gate-summary"; "regenerate" ] with
              | Ok GateSummaryRegenerateCmd -> ()
              | _ -> failtestf "Expected GateSummaryRegenerate"
          }
          test "gate-summary verify" {
              match parse [ "gate-summary"; "verify" ] with
              | Ok GateSummaryVerifyCmd -> ()
              | _ -> failtestf "Expected GateSummaryVerify"
          }
          test "gate run" {
              match parse [ "gate"; "run" ] with
              | Ok GateRunCmd -> ()
              | _ -> failtestf "Expected GateRun"
          }
          test "unknown format value is error" {
              match parse [ "source-policy"; "verify"; "--format"; "yaml" ] with
              | Error _ -> ()
              | _ -> failtestf "Expected error"
          }
          test "unknown argument is error" {
              match parse [ "source-policy"; "verify"; "--bogus" ] with
              | Error _ -> ()
              | _ -> failtestf "Expected error"
          } ]
