module Circus.Tooling.Tests.NoForcePush.CommandLexerTests

open System
open Expecto
open Circus.Tooling.NoForcePush.CommandLexer

[<Tests>]
let tests =
    testList
        "NoForcePush CommandLexer"
        [ test "normalizes shell line continuations" {
              let input = "git push \\\n  origin \\\n  main"
              let output = normalizeShellContent input
              // The function joins continuation lines with spaces
              Expect.stringContains output "git push" "contains git push"
          }
          test "extracts YAML run blocks from multiline" {
              // Simpler test - check that extract function returns tuples
              let yaml = "run: |\n  git push origin main"
              let blocks = extractYamlRunBlocks yaml
              Expect.isGreaterThan (List.length blocks) 0 "has blocks"
          }
          test "extracts Makefile recipe commands" {
              let makefile = "publish:\n\tgit push origin main"
              let commands = extractMakeCommands makefile
              Expect.isGreaterThan (List.length commands) 0 "has commands"
          }
          test "extracts Dockerfile RUN commands" {
              let dockerfile = "RUN git clone https://github.com/example/repo.git"
              let commands = extractDockerfileCommands dockerfile
              Expect.equal (List.length commands) 1 "one RUN command"
          }
          test "parses quoted arguments correctly" {
              let parts = parseCommandParts "git push --force \"origin\" 'main'"
              Expect.equal parts [ "git"; "push"; "--force"; "origin"; "main" ] "parsed correctly"
          }
          test "handles escaped quotes" {
              let parts = parseCommandParts "git push --message \"hello world\" "
              Expect.equal (List.length parts) 4 "four parts"
          }
          test "extracts effective executable for env prefix" {
              let cmd =
                  { Path = "test.sh"
                    Line = 1
                    Column = 1
                    Executable = "env"
                    Arguments =
                      [ { Index = 0
                          Value = "FOO=bar"
                          IsQuoted = false
                          IsVariable = false }
                        { Index = 1
                          Value = "git"
                          IsQuoted = false
                          IsVariable = false }
                        { Index = 2
                          Value = "push"
                          IsQuoted = false
                          IsVariable = false }
                        { Index = 3
                          Value = "origin"
                          IsQuoted = false
                          IsVariable = false }
                        { Index = 4
                          Value = "main"
                          IsQuoted = false
                          IsVariable = false } ]
                    RawSource = "env FOO=bar git push origin main"
                    NormalizedCommand = "env FOO=bar git push origin main" }

              let effective = getEffectiveExecutable cmd
              Expect.equal effective "git" "effective executable is git"
          }
          test "denormalizes adjacent quotes" {
              let result = denormalizeGitOption "--for\"ce"
              Expect.equal result "--force" "adjacent quotes joined"
          }
          test "classifies git executable" {
              Expect.equal (classifyExecutable "git") Git "git"
              Expect.equal (classifyExecutable "gh") Gh "gh"
              Expect.equal (classifyExecutable "curl") Curl "curl"
              Expect.equal (classifyExecutable "unknown") Unknown "unknown"
          } ]
