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
                                  raise (IOException "injected replacement failure")
                              else
                                  Directory.Move(source, destination) }

              match safeExtractVerified operations runner archive finalDirectory (fun _ -> Ok()) with
              | Error failure ->
                  Expect.isTrue injected "The replacement seam should have been exercised"
              | Ok path -> failtestf "A failed move must never return Ok %s" path
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
              | Error _ ->
                  Expect.isTrue
                      (File.Exists(Path.Combine(finalDirectory, "old.txt")))
                      "Extraction failure must not replace the existing installation"
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
              | Error(ExtractionFailure(_, detail)) ->
                  Expect.stringContains detail "verification failed" "The verification failure must be surfaced"
                  Expect.isTrue
                      (File.Exists(Path.Combine(finalDirectory, "old.txt")))
                      "Verification failure must restore the previous tree"

                  Expect.isFalse
                      (File.Exists(Path.Combine(finalDirectory, "new.txt")))
                      "The unverified replacement must be removed"
              | Error failure -> failtestf "Expected extraction failure, got %A" failure
              | Ok path -> failtestf "An unverified tree must never return Ok %s" path
          }

          test "a second-move that mutates then throws still leaves the previous tree live" {
              let temp, archive, finalDirectory = createFixture ()
              use cleanup = temp
              let runner = successfulArchiveRunner ()
              let mutable effectThenThrow = false

              let operations =
                  { realDirectoryOperations with
                      Move =
                          fun source destination ->
                              if
                                  not effectThenThrow
                                  && source.Contains(".circus-install-", StringComparison.Ordinal)
                                  && Path.GetFullPath(destination) = Path.GetFullPath(finalDirectory)
                              then
                                  effectThenThrow <- true
                                  // Move the candidate in, then raise after the
                                  // effect is visible on disk but before we
                                  // record success. The rollback must rely on
                                  // observed state, not on a flag that never
                                  // got set.
                                  Directory.Move(source, destination)
                                  raise (IOException "injected effect-then-throw")
                              else
                                  Directory.Move(source, destination) }

              match safeExtractVerified operations runner archive finalDirectory (fun _ -> Ok()) with
              | Error _ ->
                  Expect.isTrue
                      (File.Exists(Path.Combine(finalDirectory, "old.txt")))
                      "The previous installation must still be live when the candidate move raised after the rename"
                  Expect.isFalse
                      (File.Exists(Path.Combine(finalDirectory, "new.txt")))
                      "The partial candidate rename must be undone"
              | Ok path -> failtestf "A mid-move failure must never return Ok %s" path
          } ]
