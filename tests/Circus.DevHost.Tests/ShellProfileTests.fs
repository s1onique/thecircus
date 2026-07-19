module Circus.DevHost.Tests.ShellProfileTests

open System.IO

open Expecto
open Circus.DevHost.Adapters
open Circus.DevHost.Domain
open Circus.DevHost.ShellProfile
open Circus.DevHost.Tests.TestDoubles

let tests =
    testList
        "ShellProfile"
        [ test "applyProfile creates an absent profile and its parent directory" {
              use temp = new TempDirectory()
              let profile = Path.Combine(temp.Path, "nested", ".bashrc")
              let block = renderBlock "/opt/circus-dev" Bash
              let fileSystem = RealFilesystem() :> IFilesystem

              Expect.equal (applyProfile fileSystem profile block false) Appended "An absent profile should be created"
              Expect.equal (File.ReadAllText profile) block "The managed block should be written"
          }

          test "applyProfile replaces one existing managed block when requested" {
              use temp = new TempDirectory()
              let profile = Path.Combine(temp.Path, ".zshrc")
              let oldBlock = renderBlock "/old/circus-dev" Zsh
              let newBlock = renderBlock "/new/circus-dev" Zsh
              File.WriteAllText(profile, "export BEFORE=1\n" + oldBlock + "export AFTER=1\n")
              let fileSystem = RealFilesystem() :> IFilesystem

              Expect.equal
                  (applyProfile fileSystem profile newBlock true)
                  ReplacedExisting
                  "One managed block should reconcile in place"

              let content = File.ReadAllText profile
              Expect.stringContains content "/new/circus-dev" "The new block should be present"
              Expect.isFalse (content.Contains "/old/circus-dev") "The old block should be gone"
          }

          test "applyProfile rejects two managed blocks without rewriting the profile" {
              use temp = new TempDirectory()
              let profile = Path.Combine(temp.Path, ".bashrc")
              let block = renderBlock "/opt/circus-dev" Bash
              let original = block + "\n" + block
              File.WriteAllText(profile, original)
              let fileSystem = RealFilesystem() :> IFilesystem

              Expect.equal
                  (applyProfile fileSystem profile block true)
                  (DuplicateBlocks 2)
                  "Duplicate authority must fail closed"

              Expect.equal (File.ReadAllText profile) original "Ambiguous input must not be changed"
          }

          test "applyProfile rejects malformed marker structure" {
              use temp = new TempDirectory()
              let profile = Path.Combine(temp.Path, ".bashrc")
              let original = "export BEFORE=1\n" + beginMarker + "\nunterminated\n"
              File.WriteAllText(profile, original)
              let fileSystem = RealFilesystem() :> IFilesystem

              match applyProfile fileSystem profile (renderBlock "/opt/circus-dev" Bash) true with
              | MalformedProfile(ProfileUpdateFailure(path, _)) ->
                  Expect.equal path profile "The failure should identify the malformed profile"
                  Expect.equal (File.ReadAllText profile) original "Malformed input must not be changed"
              | outcome -> failtestf "Expected malformed-profile result, got %A" outcome
          } ]
