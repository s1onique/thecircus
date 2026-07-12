module Circus.Contracts.Tests.FixtureContractTests

open Circus.Contracts
open Circus.Contracts.Tests.Support.Fixtures

let private ok =
    EventDecoder.decode EventDecoder.DefaultMaximumBytes

/// 1. The invalid-fixture corpus does not throw for any committed file.
let testInvalidFixturesDoNotThrow () =
    let invalidPaths =
        Fixtures.allInvalidEnvelope ()
        @ Fixtures.allInvalidStarted ()
        @ Fixtures.allInvalidFinished ()

    Expect.isGreaterThan
        (List.length invalidPaths)
        0
        "invalid fixture corpus must be non-empty"

    let results =
        invalidPaths
        |> List.map (fun path ->
            let relative = path.Replace('\\', '/').Replace(Directory.GetCurrentDirectory() + "/", "")
            try
                let bytes = Fixtures.bytes relative
                let outcome = ok bytes
                match outcome with
                | Ok _ ->
                    sprintf "%s: returned Ok for invalid fixture" relative
                | Error _ ->
                    sprintf "%s: rejected as expected" relative
            with ex ->
                sprintf "%s: THREW %s" relative ex.Message)

    let unexpected = results |> List.filter (fun r -> r.EndsWith("returned Ok for invalid fixture") || r.Contains("THREW"))

    Expect.isEmpty
        unexpected
        (sprintf "All invalid fixtures must reject without throwing. Offenders:\n%s" (String.concat "\n" unexpected))

/// 2. Diagnostics do not echo back the submitted body.
let testDiagnosticsDoNotEchoBody () =
    let oversized = Fixtures.readFixture "valid/started-minimal.json"
    let hugeText = oversized + String.init 4096 (fun _ -> "a")
    let hugeBytes = System.Text.Encoding.UTF8.GetBytes hugeText :> System.ReadOnlyMemory<byte>

    let result = EventDecoder.decode 64 hugeBytes

    match result with
    | Error errs ->
        let text = errs |> NonEmptyList.toList |> List.map (sprintf "%A") |> String.concat "; "
        Expect.stringNotContains text "repository_ref" "no field values leaked into the diagnostic"
        Expect.stringNotContains text "k9b" "no body literal leaked"
    | Ok _ -> failtest "expected BodyTooLarge"

/// 3. Malformed JSON diagnostics are bounded to a small window.
let testMalformedDiagnosticsBounded () =
    let malicious =
        "{ this is not json "
        + String.init 4000 (fun _ -> "AAA")

    let bytes = System.Text.Encoding.UTF8.GetBytes malicious :> System.ReadOnlyMemory<byte>
    let result = EventDecoder.decode 262144 bytes

    match result with
    | Error errs ->
        let violation = NonEmptyList.toList errs |> List.tryPick (function
            | MalformedJson m -> Some m
            | _ -> None)

        match violation with
        | Some msg ->
            Expect.isLessThanOrEqualTo
                msg.Length
                Limits.MalformedJsonMessageLimit
                "diagnostic bounded by the documented limit"
        | None -> failtest "expected MalformedJson"
    | Ok _ -> failtest "malformed JSON must reject"

/// 4. The decoder does not retain borrowed references to disposed `JsonDocument`s.
let testDecoderDoesNotRetainDisposedDocuments () =
    let firstDecode = ok (Fixtures.bytes "valid/unknown-event.json")

    let secondDecode =
        ok (Fixtures.bytes "valid/unknown-extension.json")

    let thirdDecode =
        ok (Fixtures.bytes "valid/properties-reordered.json")

    Expect.isTrue
        (match firstDecode, secondDecode, thirdDecode with
         | Ok a, Ok b, Ok c ->
             // All three values must be independent; the fact that we
             // can inspect them after a third decode proves that no
             // references are borrowed from a disposed `JsonDocument`.
             (EventId.value a.EventId).Length > 0
             && (EventId.value b.EventId).Length > 0
             && (EventId.value c.EventId).Length > 0
         | _ -> false)
        "decoded values remain valid through subsequent decodes"

    // No mutable state should persist across calls. The Subject should
    // not leak from a previous envelope into a later one.
    let onlySubjectA = firstDecode |> function
        | Ok v -> v.Subject = "run/019b0437-1ef2-7abc-a38d-23472513f50a"
        | Error _ -> false

    let onlySubjectB = secondDecode |> function
        | Ok v -> v.Subject = "run/019b0437-1ef2-7abc-a38d-23472513f50b"
        | Error _ -> false

    Expect.isTrue onlySubjectA "subject A isolated"
    Expect.isTrue onlySubjectB "subject B isolated"

let tests =
    testList
        "Safety"
        [
            test "invalid fixture corpus does not throw" testInvalidFixturesDoNotThrow
            test "diagnostics do not echo full body" testDiagnosticsDoNotEchoBody
            test "malformed diagnostics are bounded" testMalformedDiagnosticsBounded
            test "decoder does not retain disposed documents" testDecoderDoesNotRetainDisposedDocuments
        ]
