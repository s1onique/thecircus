module Circus.Tooling.Tests.NoForcePush.GateWiringTests

open System
open System.IO
open Expecto

[<Tests>]
let tests =
    testList
        "NoForcePush GateWiring"
        [ test "verify command exists in CLI" {
              // Verify that the no-force-push subcommand is properly registered
              let args = [ "no-force-push"; "verify" ]
              match Circus.Tooling.NoForcePush.Cli.parse args with
              | Ok(Circus.Tooling.NoForcePush.Cli.VerifyCmd _) -> ()
              | Ok _ -> failwith "wrong command type"
              | Error e -> failwithf "parse error: %s" e
          }
          test "pre-push command exists in CLI" {
              let args = [ "no-force-push"; "pre-push"; "--repo"; "/tmp"; "--remote-name"; "origin"; "--remote-url"; "git@x:y.git" ]
              match Circus.Tooling.NoForcePush.Cli.parse args with
              | Ok(Circus.Tooling.NoForcePush.Cli.PrePushCmd _) -> ()
              | Ok _ -> failwith "wrong command type"
              | Error e -> failwithf "parse error: %s" e
          }
          test "github-rules command exists in CLI" {
              let args = [ "no-force-push"; "github-rules"; "verify"; "--repository"; "o/r"; "--branch"; "main" ]
              match Circus.Tooling.NoForcePush.Cli.parse args with
              | Ok(Circus.Tooling.NoForcePush.Cli.GitHubRulesCmd _) -> ()
              | Ok _ -> failwith "wrong command type"
              | Error e -> failwithf "parse error: %s" e
          }
          test "Top-level CLI routes to no-force-push" {
              // This tests that Program.fs routes correctly
              let args = [ "no-force-push"; "verify" ]
              match Circus.Tooling.SourcePolicy.Cli.parseTopLevel args with
              | Ok(Circus.Tooling.SourcePolicy.Cli.NoForcePushCmd _) -> ()
              | Ok _ -> failwith "wrong command type"
              | Error e -> failwithf "parse error: %s" e
          }
          test "StaticPolicy.verify returns structured result" {
              // Test against real repo
              let root = Directory.GetCurrentDirectory()
              let result = Circus.Tooling.NoForcePush.StaticPolicy.verify root
              Expect.isNotNull result "result is not null"
              Expect.isGreaterThan result.FilesExamined 0 "files examined"
              // Diagnostics may be empty or populated depending on state
              Expect.isNotNull result.Diagnostics "diagnostics list exists"
              Expect.isNotNull result.OperationalErrors "errors list exists"
          }
          test "Exit codes are correct" {
              Expect.equal Circus.Tooling.NoForcePush.Cli.ExitCode.pass 0 "pass = 0"
              Expect.equal Circus.Tooling.NoForcePush.Cli.ExitCode.policyFailure 1 "policyFailure = 1"
              Expect.equal Circus.Tooling.NoForcePush.Cli.ExitCode.operationalError 2 "operationalError = 2"
          }
          test "StaticPolicy verify produces deterministic output" {
              let root = Directory.GetCurrentDirectory()
              let result1 = Circus.Tooling.NoForcePush.StaticPolicy.verify root
              let result2 = Circus.Tooling.NoForcePush.StaticPolicy.verify root
              
              let diagCount1 = List.length result1.Diagnostics
              let diagCount2 = List.length result2.Diagnostics
              Expect.equal diagCount1 diagCount2 "diagnostic count is deterministic"
          } ]
