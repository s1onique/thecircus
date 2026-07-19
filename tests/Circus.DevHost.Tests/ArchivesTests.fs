module Circus.DevHost.Tests.ArchivesTests

open System
open System.IO

open Expecto
open Circus.DevHost.Archives
open Circus.DevHost.Domain
open Circus.DevHost.ProcessRunner
open Circus.DevHost.Tests.TestDoubles

let private successfulArchiveRunner () =
    StubProcessRunner(fun specification ->
        if List.contains "-tzf" specification.Arguments then
            Ok(processResult "payload/new.txt")
        elif List.contains "-xzf" specification.Arguments then
            let destinationIndex = List.findIndex ((=) "-C") specification.Arguments + 1
            let destination = specification.Arguments.[destinationIndex]
            File.WriteAllText(Path.Combine(destination, "new.txt"), "new")
            Ok(processResult "")
        else
            Error(ProcessExitFailure(specification.FileName, 1, "unexpected invocation")))
    :> IProcessRunner

let private createFixture () =
    let temp = new TempDirectory()
    let archive = Path.Combine(temp.Path, "fixture.tar.gz")
    let finalDirectory = Path.Combine(temp.Path, "installed")
    File.WriteAllText(archive, "fixture")
    Directory.CreateDirectory finalDirectory |> ignore
    File.WriteAllText(Path.Combine(finalDirectory, "old.txt"), "old")
    temp, archive, finalDirectory

let private assertOld (finalDirectory: string) =
    Expect.isTrue
        (File.Exists(Path.Combine(finalDirectory, "old.txt")))
        "The previous installation's old.txt must be present after recovery"
    Expect.isFalse
        (File.Exists(Path.Combine(finalDirectory, "new.txt")))
        "The failed candidate's new.txt must not be visible after recovery"

let tests =
    testList
        "Archives"
        [ test "a failed replacement move reports Error and restores the previous tree" {
              let temp, archive, finalDirectory = createFixture ()
              use cleanup = temp
              let runner = successfulArchiveRunner ()
              let mutable injected = false

              let operations =
                  { realDirectoryOperations with
                      Move =
                          fun source destination ->
                              if
                                  not injected
                                  && source.Contains(".circus-install-", StringComparison.Ordinal)
                                  && Path.GetFullPath(destination) = Path.GetFullPath(finalDirectory)
                              then
                                  injected <- true
                                  // Move the candidate in and then throw; the
                                  // effect is visible on disk but we never
                                  // recorded success. The recovery must rely
                                  // on observed state, not on a flag.
                                  Directory.Move(source, destination)
                                  raise (IOException "injected replacement failure")
                              else
                                  Directory.Move(source, destination) }

              match safeExtractVerified operations runner archive finalDirectory (fun _ -> Ok()) with
              | Error _ ->
                  Expect.isTrue injected "The replacement seam should have been exercised"
                  assertOld finalDirectory
              | Ok path -> failtestf "A failed move must never return Ok %s" path
          }

          test "a failed second-move that mutates then throws leaves the previous tree live" {
              let temp, archive, finalDirectory = createFixture ()
              use cleanup = temp
              let runner = successfulArchiveRunner ()
              let mutable injected = false

              let operations =
                  { realDirectoryOperations with
                      Move =
                          fun source destination ->
                              if
                                  not injected
                                  && source.Contains(".circus-install-", StringComparison.Ordinal)
                                  && Path.GetFullPath(destination) = Path.GetFullPath(finalDirectory)
                              then
                                  injected <- true
                                  Directory.Move(source, destination)
                                  raise (IOException "injected effect-then-throw")
                              else
                                  Directory.Move(source, destination) }

              match safeExtractVerified operations runner archive finalDirectory (fun _ -> Ok()) with
              | Error _ ->
                  Expect.isTrue injected "The move seam should have been exercised"
                  assertOld finalDirectory
              | Ok path -> failtestf "A mid-move failure must never return Ok %s" path
          }

          test "an extraction failure preserves the previous tree" {
              let temp, archive, finalDirectory = createFixture ()
              use cleanup = temp

              let runner =
                  StubProcessRunner(fun specification ->
                      if List.contains "-tzf" specification.Arguments then
                          Ok(processResult "payload/new.txt")
                      else
                          Error(ProcessExitFailure("tar", 2, "injected extraction failure")))
                  :> IProcessRunner

              match safeExtractVerified realDirectoryOperations runner archive finalDirectory (fun _ -> Ok()) with
              | Error _ -> assertOld finalDirectory
              | Ok path -> failtestf "A failed extraction must never return Ok %s" path
          }

          test "a verification failure rolls the previous tree back" {
              let temp, archive, finalDirectory = createFixture ()
              use cleanup = temp
              let runner = successfulArchiveRunner ()

              match
                  safeExtractVerified realDirectoryOperations runner archive finalDirectory (fun _ ->
                      Error(VerificationFailure "injected verification failure"))
              with
              | Error _ -> assertOld finalDirectory
              | Ok path -> failtestf "An unverified tree must never return Ok %s" path
          }

          test "a cold-start install with a failed second move leaves no stale directories" {
              let temp = new TempDirectory ()
              use cleanup = temp
              let archive = Path.Combine(temp.Path, "fixture.tar.gz")
              let finalDirectory = Path.Combine(temp.Path, "installed")
              File.WriteAllText(archive, "fixture")
              // No prior install present: hadPrevious is false.
              let runner = successfulArchiveRunner ()
              let mutable injected = false

              let operations =
                  { realDirectoryOperations with
                      Move =
                          fun source destination ->
                          if
                              not injected
                              && source.Contains(".circus-install-", StringComparison.Ordinal)
                              && Path.GetFullPath(destination) = Path.GetFullPath(finalDirectory)
                          then
                              injected <- true
                              Directory.Move(source, destination)
                              raise (IOException "injected effect-then-throw")
                          else
                              Directory.Move(source, destination) }

              match safeExtractVerified operations runner archive finalDirectory (fun _ -> Ok()) with
              | Error _ ->
                  Expect.isTrue injected "The move seam should have been exercised"
                  Expect.isTrue
                      (not (Directory.Exists finalDirectory))
                      "A cold-start failure must not leave a candidate on disk"
              | Ok path -> failtestf "A failed cold-start must never return Ok %s" path
          } ]
