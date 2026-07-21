module Circus.DevHost.Tests.LauncherPolicyTests

open System
open System.IO

open Expecto
open Circus.DevHost.Domain
open Circus.DevHost.ToolchainManifest

let private repoRoot () : string =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private loadManifest () : ToolchainData =
    let manifestPath = Path.Combine(repoRoot (), "eng", "devhost-toolchain.json")
    Expect.isTrue (File.Exists manifestPath) "The manifest must exist"
    Manifest.parse (File.ReadAllText manifestPath)

let private imageString () : string =
    let manifest = loadManifest ()

    let image =
        manifest.BootstrapSdkImage
        |> Option.defaultWith (fun () -> failtest "bootstrap_sdk_image is required")

    sprintf "%s@%s" image.Reference image.Digest

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
                  (text.Contains("jq ") || text.Contains("jq -") || text.Contains("$(jq"))
                  "The launcher must not shell out to jq"
          }

          test "the launcher pins the bootstrap image as reference@sha256" {
              let launcher = Path.Combine(repoRoot (), "scripts", "circus-dev")
              let text = File.ReadAllText launcher
              let expected = sprintf "BOOTSTRAP_IMAGE='%s'" (imageString ())
              Expect.isTrue (text.Contains expected) (sprintf "The launcher must pin the manifest image as %s" expected)
          }

          test "a manifest image mutation breaks the launcher equality test" {
              let launcher = Path.Combine(repoRoot (), "scripts", "circus-dev")
              let launcherText = File.ReadAllText launcher

              let launcherMatches (manifest: ToolchainData) =
                  match manifest.BootstrapSdkImage with
                  | Some image -> launcherText.Contains(sprintf "BOOTSTRAP_IMAGE='%s@%s'" image.Reference image.Digest)
                  | None -> false

              let committed = loadManifest ()
              Expect.isTrue (launcherMatches committed) "The committed launcher must match the committed manifest"

              let baseImage =
                  committed.BootstrapSdkImage
                  |> Option.defaultWith (fun () -> failtest "bootstrap_sdk_image is required")

              let mutatedDigest =
                  { baseImage with
                      Digest = "sha256:" + String.replicate 64 "a" }

              let mutated =
                  { committed with
                      BootstrapSdkImage = Some mutatedDigest }

              Expect.isFalse
                  (launcherMatches mutated)
                  "A manifest digest mutation must break the launcher equality assertion"

              let mutatedReference =
                  { baseImage with
                      Reference = "mcr.microsoft.com/dotnet/sdk:9.0.999-fake" }

              let mutatedRef =
                  { committed with
                      BootstrapSdkImage = Some mutatedReference }

              Expect.isFalse
                  (launcherMatches mutatedRef)
                  "A manifest reference mutation must break the launcher equality assertion"
          }

          test "the committed manifest parses and validates" {
              let manifest = loadManifest ()

              match validate manifest with
              | Ok() -> ()
              | Error failure -> failtestf "Manifest must validate, got %s" (renderFailure failure)
          }

          test "a non-hex manifest digest is rejected" {
              let manifest = loadManifest ()

              let image =
                  manifest.BootstrapSdkImage
                  |> Option.defaultWith (fun () -> failtest "bootstrap_sdk_image is required")

              let attempt =
                  { image with
                      Digest = "sha256:" + String.replicate 64 "?" }

              let mutated =
                  { manifest with
                      BootstrapSdkImage = Some attempt }

              match validate mutated with
              | Error _ -> ()
              | Ok() -> failtestf "Non-hex manifest digest must be rejected"
          } ]
