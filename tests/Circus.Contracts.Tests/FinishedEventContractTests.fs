module Circus.Contracts.Tests.FinishedEventContractTests

/// Silence warning FS3391 about implicit byte[] -> ReadOnlyMemory<byte> conversion
/// because we explicitly construct ReadOnlyMemory<byte> via Fixtures.bytes/inlineBytes.
/// Treat that warning as informational in the test project.
#nowarn "3391"


open Expecto
open Circus.Contracts
open Circus.Domain
open Circus.Contracts.Tests.Support.Fixtures

let private decodeOk relativePath : ExecutionFinished =
    match EventDecoder.decode EventDecoder.DefaultMaximumBytes (Fixtures.bytes relativePath) with
    | Ok validated ->
        match validated.Event with
        | ExecutionFinishedEvent finished -> finished
        | other ->
            failwithf "expected ExecutionFinishedEvent for %s, got %A" relativePath other
    | Error errs ->
        let msg = errs |> NonEmptyList.toList |> List.map (sprintf "%A") |> String.concat "; "
        failwithf "expected Ok finished event for %s, got errors: %s" relativePath msg

/// 1. Each recognised outcome decodes with the canonical wire form preserved.
let testEveryOutcomeDecodes () =
    let succeeded = decodeOk "valid/finished-succeeded.json"
    let failed = decodeOk "valid/finished-failed.json"

    Expect.equal succeeded.Outcome Succeeded "succeeded outcome"
    Expect.equal failed.Outcome Failed "failed outcome"
    Expect.equal (ExecutionOutcome.toWire Failed) "failed" "wire form"

/// 2. `cancelled` and `timed_out` outcomes decode via inline envelopes.
let testCancelledAndTimedOutOutcomes () =
    let buildEnvelope runid outcome (duration: int) =
        sprintf
            """{"specversion":"1.0","id":"%s","source":"urn:leamas:instance:builder-07","type":"io.leamas.execution.finished.v1","subject":"%s","time":"2026-07-12T20:00:00Z","datacontenttype":"application/json","circusinstance":"builder-07","circusepoch":"019b0400-2f61-720d-94a5-c84e928eae19","circusseq":555,"runid":"%s","data":{"outcome":"%s","duration_ms":%d,"checks":{"passed":1,"failed":0,"skipped":0}}}"""
            runid
            (sprintf "run/%s" runid)
            runid
            outcome
            duration

    let bytesFor (s: string) = System.Text.Encoding.UTF8.GetBytes s

    let cancelledEnv = buildEnvelope "019b0437-1ef2-7abc-a38d-23472513f520" "cancelled" 0
    let timedOutEnv = buildEnvelope "019b0437-1ef2-7abc-a38d-23472513f521" "timed_out" 604800000

    let cancelled =
        EventDecoder.decode EventDecoder.DefaultMaximumBytes (bytesFor cancelledEnv)

    let timedOut =
        EventDecoder.decode EventDecoder.DefaultMaximumBytes (bytesFor timedOutEnv)

    let extractOutcome (r: ValidationResult<ValidatedEvent>) =
        match r with
        | Ok v ->
            match v.Event with
            | ExecutionFinishedEvent f -> Some f.Outcome
            | _ -> None
        | _ -> None

    Expect.equal (extractOutcome cancelled) (Some Cancelled) "cancelled outcome"
    Expect.equal (extractOutcome timedOut) (Some TimedOut) "timed_out outcome"

/// 3. Unknown outcome strings are rejected.
let testUnknownOutcomeRejected () =
    let result =
        EventDecoder.decode
            EventDecoder.DefaultMaximumBytes
            (Fixtures.bytes "invalid-finished/finished-unknown-outcome.json")

    let violations = Assertions.contractViolations result
    Expect.isTrue (Assertions.hasInvalidKnownPayload violations) "InvalidKnownPayload present"

    let payloadErrs = Assertions.payloadViolations violations
    Expect.isTrue
        (payloadErrs
         |> List.exists (function
             | PayloadInvalidFieldValue (n, _) when n = "outcome" -> true
             | _ -> false))
        "PayloadInvalidFieldValue on outcome"

/// 4. Zero duration is valid.
let testZeroDurationValid () =
    let finished = decodeOk "valid/finished-succeeded.json"
    Expect.isTrue (finished.DurationMilliseconds >= 0L) "duration_ms non-negative"

/// 5. Maximum duration (one week in ms) is valid.
let testMaximumDurationValid () =
    let finished = decodeOk "valid/finished-failed.json"
    Expect.isTrue
        (finished.DurationMilliseconds <= Limits.DurationMaxMilliseconds)
        "duration_ms ≤ week"

/// 6. Excessive duration is rejected.
let testExcessiveDurationRejected () =
    let bogus = """{"specversion":"1.0","id":"019b0437-2766-7a20-9225-4ab1645ba141","source":"urn:leamas:instance:builder-07","type":"io.leamas.execution.finished.v1","subject":"run/019b0437-1ef2-7abc-a38d-23472513f521","time":"2026-07-12T20:00:00Z","datacontenttype":"application/json","circusinstance":"builder-07","circusepoch":"019b0400-2f61-720d-94a5-c84e928eae19","circusseq":556,"runid":"019b0437-1ef2-7abc-a38d-23472513f521","data":{"outcome":"succeeded","duration_ms":604800001,"checks":{"passed":0,"failed":0,"skipped":0}}}"""
    let bytes = System.Text.Encoding.UTF8.GetBytes bogus
    let result = EventDecoder.decode EventDecoder.DefaultMaximumBytes bytes
    let payloadErrs = Assertions.payloadViolations (Assertions.contractViolations result)

    Expect.isTrue
        (payloadErrs
         |> List.exists (function
             | PayloadInvalidFieldValue (n, _) when n = "duration_ms" -> true
             | _ -> false))
        "excessive duration rejected"

/// 7. Negative counts are rejected.
let testNegativeCountsRejected () =
    let bogus = """{"specversion":"1.0","id":"019b0437-2766-7a20-9225-4ab1645ba142","source":"urn:leamas:instance:builder-07","type":"io.leamas.execution.finished.v1","subject":"run/019b0437-1ef2-7abc-a38d-23472513f522","time":"2026-07-12T20:00:00Z","datacontenttype":"application/json","circusinstance":"builder-07","circusepoch":"019b0400-2f61-720d-94a5-c84e928eae19","circusseq":557,"runid":"019b0437-1ef2-7abc-a38d-23472513f522","data":{"outcome":"succeeded","duration_ms":1000,"checks":{"passed":-1,"failed":0,"skipped":0}}}"""
    let bytes = System.Text.Encoding.UTF8.GetBytes bogus
    let result = EventDecoder.decode EventDecoder.DefaultMaximumBytes bytes
    let payloadErrs = Assertions.payloadViolations (Assertions.contractViolations result)

    Expect.isTrue
        (payloadErrs
         |> List.exists (function
             | PayloadInvalidFieldValue (n, _) when n = "passed" -> true
             | _ -> false))
        "negative count rejected"

/// 8. Excessive counts (>1_000_000) are rejected.
let testExcessiveCountsRejected () =
    let result =
        EventDecoder.decode
            EventDecoder.DefaultMaximumBytes
            (Fixtures.bytes "invalid-finished/finished-invalid-check-counts.json")

    let payloadErrs = Assertions.payloadViolations (Assertions.contractViolations result)
    Expect.isTrue
        (payloadErrs
         |> List.exists (function
             | PayloadInvalidFieldValue (n, _) when n = "passed" -> true
             | _ -> false))
        "excessive count rejected"

/// 9. Optional `summary` is preserved when present.
let testOptionalSummaryPreserved () =
    let finished = decodeOk "valid/finished-failed.json"
    Expect.equal
        finished.Summary
        (Some "Two checks failed: outcome decoding, payload validation")
        "summary preserved"

let tests =
    testList
        "Finished Event Contract"
        [
            test "every execution outcome decodes" testEveryOutcomeDecodes
            test "cancelled and timed_out outcomes decode" testCancelledAndTimedOutOutcomes
            test "unknown outcome is rejected" testUnknownOutcomeRejected
            test "zero duration is valid" testZeroDurationValid
            test "maximum duration is valid" testMaximumDurationValid
            test "excessive duration is rejected" testExcessiveDurationRejected
            test "negative counts are rejected" testNegativeCountsRejected
            test "excessive counts are rejected" testExcessiveCountsRejected
            test "optional summary is preserved" testOptionalSummaryPreserved
        ]
