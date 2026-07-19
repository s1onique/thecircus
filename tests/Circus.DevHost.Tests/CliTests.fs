module Circus.DevHost.Tests.CliTests

open Expecto
open Circus.DevHost.Cli
open Circus.DevHost.Domain

let tests =
    testList
        "CLI"
        [ test "parse accepts every supported bootstrap and doctor flag ordering" {
              let cases =
                  [ [ "bootstrap" ], Bootstrap(false, false)
                    [ "bootstrap"; "--force" ], Bootstrap(true, false)
                    [ "bootstrap"; "--dry-run" ], Bootstrap(false, true)
                    [ "bootstrap"; "--force"; "--dry-run" ], Bootstrap(true, true)
                    [ "bootstrap"; "--dry-run"; "--force" ], Bootstrap(true, true)
                    [ "doctor" ], Doctor(false, false)
                    [ "doctor"; "--json" ], Doctor(true, false)
                    [ "doctor"; "--allow-dirty" ], Doctor(false, true)
                    [ "doctor"; "--json"; "--allow-dirty" ], Doctor(true, true)
                    [ "doctor"; "--allow-dirty"; "--json" ], Doctor(true, true) ]

              for arguments, expected in cases do
                  Expect.equal (parse arguments) (Ok expected) (String.concat " " arguments)
          }

          test "env supports automatic detection by omitting --shell" {
              Expect.equal (parse [ "env" ]) (Ok(Env None)) "No shell means detect at execution time"
          }

          test "env accepts bash and zsh explicitly" {
              Expect.equal (parse [ "env"; "--shell"; "bash" ]) (Ok(Env(Some Bash))) "bash"
              Expect.equal (parse [ "env"; "--shell"; "zsh" ]) (Ok(Env(Some Zsh))) "zsh"
          }

          test "env rejects the unimplemented auto shell token" {
              match parse [ "env"; "--shell"; "auto" ] with
              | Error message -> Expect.stringContains message "unsupported shell" "auto must not be advertised"
              | Ok command -> failtestf "Expected rejection, got %A" command
          }

          test "duplicate and unknown flags fail closed" {
              for arguments in [ [ "bootstrap"; "--force"; "--force" ]; [ "doctor"; "--json"; "--json" ] ] do
                  match parse arguments with
                  | Error _ -> ()
                  | Ok command -> failtestf "Expected rejection for %A, got %A" arguments command
          } ]
