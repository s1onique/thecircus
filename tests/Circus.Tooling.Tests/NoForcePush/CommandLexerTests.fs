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
              Expect.equal output "git push   origin   main" "continuations joined"
          }
          test "extracts YAML run blocks" {
              let yaml = """
name: test
on: push
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - run: |
          git push origin main
          echo done
"""
              let blocks = extractYamlRunBlocks yaml
              Expect.equal (List.length blocks) 1 "one block"
              Expect.stringContains blocks.Head "git push origin main" "contains push"
          }
          test "extracts Makefile recipe commands" {
              let makefile = """
publish:
\tgit push origin main
\techo done
"""
              let commands = extractMakeCommands makefile
              Expect.isGreaterThan (List.length commands) 0 "has commands"
              Expect.stringContains commands.Head "git push" "contains git push"
          }
          test "extracts Dockerfile RUN commands" {
              let dockerfile = """
FROM alpine
RUN git clone https://github.com/example/repo.git
RUN apk add --no-cache git
"""
              let commands = extractDockerfileCommands dockerfile
              Expect.equal (List.length commands) 2 "two RUN commands"
          }
          test "parses quoted arguments correctly" {
              let parts = parseCommandParts """git push --force "origin" 'main'"""
              Expect.equal parts [ "git"; "push"; "--force"; "origin"; "main" ] "parsed correctly"
          }
          test "handles escaped quotes" {
              let parts = parseCommandParts """git push --message "hello \"world\""""
              Expect.equal (List.length parts) 4 "four parts"
          }
          test "extracts effective executable for env prefix" {
              let cmd = {
                  Executable = "env"
                  Arguments = [ "FOO=bar"; "git"; "push"; "origin"; "main" ]
                  SourceLocation = { Line = 1; Column = 1; AbsoluteOffset = 0 }
                  RawSource = "env FOO=bar git push origin main"
              }
              let effective = getEffectiveExecutable cmd
              Expect.equal effective "git" "effective executable is git"
          }
          test "denormalizes adjacent quotes" {
              let result = denormalizeGitOption """--for"ce"""
              Expect.equal result "--force" "adjacent quotes joined"
          }
          test "classifies git executable" {
              Expect.equal (classifyExecutable "git") Git "git"
              Expect.equal (classifyExecutable "git.exe") Git "git.exe"
              Expect.equal (classifyExecutable "gh") Gh "gh"
              Expect.equal (classifyExecutable "curl") Curl "curl"
              Expect.equal (classifyExecutable "unknown") Unknown "unknown"
          } ]
