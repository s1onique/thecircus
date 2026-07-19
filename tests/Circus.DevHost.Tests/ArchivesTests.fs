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
        [ test "an ordinary failed second-move (pre-effect) preserves the previous tree" {
              let temp, archive, finalDirectory = createFixture ()
              use cleanup = temp
              let runner = successfulArchiveRunner ()
              let mutable injected = false

              // The first move (`absoluteFinal -> previousDir`) succeeds, but
              // the second move (`installDir -> absoluteFinal`) throws
              // before renaming the candidate. The recovery must leave the
              // previous install reachable.
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
                                  raise (IOException "ordinary pre-effect failure")
                              else
                                  Directory.Move(source, destination) }

              match safeExtractVerified operations runner archive finalDirectory (fun _ -> Ok()) with
              | Error _ ->
                  Expect.isTrue injected "The ordinary pre-effect seam should have been exercised"
                  assertOld finalDirectory
              | Ok path -> failtestf "A pre-effect failure must never return Ok %s" path
          }

          test "an ordinary failed first-move (pre-effect) preserves the previous tree" {
              let temp, archive, finalDirectory = createFixture ()
              use cleanup = temp
              let runner = successfulArchiveRunner ()
              let mutable injected = false

              // The very first move (`absoluteFinal -> previousDir`) throws
              // before renaming. The recovery must leave the previous
              // install in `absoluteFinal` and the previous-temp absent.
              let operations =
                  { realDirectoryOperations with
                      Move =
                          fun source destination ->
                              if
                                  not injected
                                  && Path.GetFullPath(source) = Path.GetFullPath(finalDirectory)
                              then
                                  injected <- true
                                  raise (IOException "ordinary pre-effect failure (first move)")
                              else
                                  Directory.Move(source, destination) }

              match safeExtractVerified operations runner archive finalDirectory (fun _ -> Ok()) with
              | Error _ ->
                  Expect.isTrue injected "The ordinary first-move seam should have been exercised"
                  assertOld finalDirectory
              | Ok path -> failtestf "A pre-effect failure must never return Ok %s" path
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

          test "a cold-start install with a failed second move does not leak the candidate" {
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
                  let installDirs =
                      Directory.GetDirectories(temp.Path, ".circus-install-*")
                  Expect.isEmpty installDirs "A cold-start failure must not leak a .circus-install-* directory"
              | Ok path -> failtestf "A failed cold-start must never return Ok %s" path
          }

          test "a failed delete of the candidate leaves the previous installation reachable" {
              // When the candidate is already in `absoluteFinal` and the
              // restore path tries (and fails) to delete it, the previous
              // installation must remain on disk for a human operator to
              // recover.
              let temp, archive, finalDirectory = createFixture ()
              use cleanup = temp
              let runner = successfulArchiveRunner ()
              let mutable failDelete = false

              let operations =
                  { realDirectoryOperations with
                      Delete =
                          fun path ->
                              if
                                  not failDelete
                                  && Path.GetFullPath(path) = Path.GetFullPath(finalDirectory)
                              then
                                  failDelete <- true
                                  raise (IOException "injected delete failure")
                              else
                                  if Directory.Exists path then
                                      Directory.Delete(path, true) }

              match
                  safeExtractVerified
                      operations
                      runner
                      archive
                      finalDirectory
                      (fun _ -> Error(VerificationFailure "verification failed"))
              with
              | Error detail ->
                  Expect.stringContains
                    (string detail)
                    "rollback incomplete; previous installation retained at"
                    "The error detail must announce that the previous installation is retained"
                  Expect.isTrue
                      (Directory.Exists finalDirectory)
                      "The final directory must still exist on disk after a failed candidate delete"
              | Ok path -> failtestf "A delete-failed rollback must never return Ok %s" path
          } ]
