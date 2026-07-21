module Circus.Tooling.Tests.NoForcePush.CliTests

open Expecto
open Circus.Tooling.NoForcePush.Cli

[<Tests>]
let tests =
    testList
        "NoForcePush Cli"
        [ test "parses verify command" {
              match parse [ "verify" ] with
              | Ok(VerifyCmd "human") -> ()
              | Ok _ -> failwith "wrong format"
              | Error e -> failwithf "parse error: %s" e
          }
          test "parses verify json format" {
              match parse [ "verify"; "--format"; "json" ] with
              | Ok(VerifyCmd "json") -> ()
              | Ok _ -> failwith "wrong format"
              | Error e -> failwithf "parse error: %s" e
          }
          test "parses pre-push command" {
              match parse [ "pre-push"; "--repo"; "/path/to/repo"; "--remote-name"; "origin"; "--remote-url"; "git@github.com:o/r.git" ] with
              | Ok(PrePushCmd(repo, remote, url)) ->
                  Expect.equal repo "/path/to/repo" "repo"
                  Expect.equal remote "origin" "remote"
                  Expect.equal url "git@github.com:o/r.git" "url"
              | Error e -> failwithf "parse error: %s" e
              | Ok _ -> failwith "wrong command"
          }
          test "parses github-rules verify command" {
              match parse [ "github-rules"; "verify"; "--repository"; "s1onique/thecircus"; "--branch"; "main" ] with
              | Ok(GitHubRulesCmd(repo, branch)) ->
                  Expect.equal repo "s1onique/thecircus" "repo"
                  Expect.equal branch "main" "branch"
              | Error e -> failwithf "parse error: %s" e
              | Ok _ -> failwith "wrong command"
          }
          test "parses help command" {
              match parse [ "help" ] with
              | Ok HelpCmd -> ()
              | Ok _ -> failwith "wrong command"
              | Error e -> failwithf "parse error: %s" e
          }
          test "rejects unknown subcommand" {
              match parse [ "unknown" ] with
              | Ok _ -> failwith "should fail"
              | Error _ -> ()
          }
          test "rejects missing pre-push args" {
              match parse [ "pre-push"; "--repo"; "/path" ] with
              | Ok _ -> failwith "should fail"
              | Error _ -> ()
          }
          test "rejects duplicate pre-push args" {
              match parse [ "pre-push"; "--repo"; "/path"; "--repo"; "/other"; "--remote-name"; "origin"; "--remote-url"; "url" ] with
              | Ok _ -> failwith "should fail"
              | Error _ -> ()
          }
          test "helpText is non-empty" {
              let text = helpText()
              Expect.isNonEmpty text "help text"
              Expect.stringContains text "no-force-push" "mentions no-force-push"
          } ]
