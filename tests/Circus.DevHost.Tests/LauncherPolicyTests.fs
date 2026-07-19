module Circus.DevHost.Tests.LauncherPolicyTests

open System.IO

open Expecto
open Circus.DevHost.Domain
open Circus.DevHost.ToolchainManifest

let private repoRoot () : string =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let tests =
    testList
        "Launcher"
        [ test "scripts/circus-dev is free of Python and JSON parsers" {
              let launcher = Path.Combine(repoRoot (), "scripts", "circus-dev")
              Expect.isTrue (File.Exists launcher) "The launcher must exist"
              let text = File.ReadAllText launcher
              Expect.isFalse
                  (text.Contains("python3")
                   || text.Contains("python ")
                   || text.Contains(" python")
                   || text.Contains("import json"))
                  "The launcher must not invoke Python or parse JSON"
              Expect.isFalse
                  (text.Contains("jq ")
                   || text.Contains("jq -")
                   || text.Contains("$(jq"))
                  "The launcher must not shell out to jq"
          }

          test "the launcher pins the bootstrap image as reference@sha256" {
              let launcher = Path.Combine(repoRoot (), "scripts", "circus-dev")
              let text = File.ReadAllText launcher
              let expected = "mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616"
              Expect.isTrue
                  (text.Contains(expected))
                  "The launcher must pin the manifest reference and digest as reference@sha256"
          }

          test "the committed manifest parses and validates" {
              let manifestPath = Path.Combine(repoRoot (), "eng", "devhost-toolchain.json")
              Expect.isTrue (File.Exists manifestPath) "The manifest must exist"

              let manifest = Manifest.parse (File.ReadAllText manifestPath)
              match validate manifest with
              | Ok() -> ()
              | Error failure -> failtestf "Manifest must validate, got %s" (renderFailure failure)
          } ]
