module Circus.DevHost.Tests.DownloadsTests

open System.IO
open System.Threading

open Expecto
open Circus.DevHost.Domain
open Circus.DevHost.Downloads
open Circus.DevHost.Integrity
open Circus.DevHost.Tests.TestDoubles

type private PayloadHttp(payload: string) =
    interface IHttp with
        member _.Download(_uri, destination, expected, _cancellation) =
            async {
                File.WriteAllText(destination, payload)

                match verifyDownloaded destination expected with
                | Ok() ->
                    return
                        Ok
                            { Path = destination
                              Bytes = FileInfo(destination).Length
                              ContentType = Some "text/plain" }
                | Error failure ->
                    File.Delete destination
                    return Error failure
            }

let private download payload expected destination =
    let http = PayloadHttp(payload) :> IHttp
    Async.RunSynchronously(http.Download("fixture://payload", destination, expected, CancellationToken.None))

let tests =
    testList
        "Downloads"
        [ test "a fake HTTP payload is accepted with its known SHA-256" {
              use temp = new TempDirectory()
              let destination = Path.Combine(temp.Path, "payload.txt")
              let expected = sha256OfString "known payload"

              match download "known payload" (Sha256 expected) destination with
              | Ok result ->
                  Expect.equal result.Path destination "The downloaded path should be returned"
                  Expect.isTrue (File.Exists destination) "Verified payload should remain"
              | Error failure -> failtestf "Expected success, got %A" failure
          }

          test "a fake HTTP payload with a different SHA-256 fails closed" {
              use temp = new TempDirectory()
              let destination = Path.Combine(temp.Path, "payload.txt")
              let differentHash = sha256OfString "different payload"

              match download "known payload" (Sha256 differentHash) destination with
              | Error(IntegrityFailure _) ->
                  Expect.isFalse (File.Exists destination) "A rejected payload should be removed"
              | Error failure -> failtestf "Expected integrity failure, got %A" failure
              | Ok result -> failtestf "Expected rejection, got %A" result
          }

          test "NoPayloadHash explicitly permits metadata without a payload digest" {
              use temp = new TempDirectory()
              let destination = Path.Combine(temp.Path, "metadata.json")

              match download "{\"schema\":1}" NoPayloadHash destination with
              | Ok result -> Expect.equal result.Bytes 12L "Metadata bytes should be reported"
              | Error failure -> failtestf "Expected success, got %A" failure
          } ]
