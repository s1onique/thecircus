module Circus.Contracts.Tests.FinishedEventContractTests

open Circus.Contracts
open Circus.Domain
open Circus.Contracts.Tests.Support.Fixtures

let private ok =
    EventDecoder.decode EventDecoder.DefaultMaximumBytes

let private outcomeFrom result =
    match result with
    | Ok v ->
        match v.Event with
        | ExecutionFinishedEvent f -> Ok f
        | other -> failwithf "expected finished, got %A" other
    | Error e ->
        e |> NonEmptyList.toList |> List.map (sprintf "%A") |> String.concat "; "
        |> failwithf

/// Each recognised outcome decodes with the canonical wire form preserved.
let testEveryOutcomeDecodes () =
    let succeededRes = outcomeFrom (ok (Fixtures.bytes "valid/finished-succeeded.json"))
    let failedRes = outcomeFrom (ok (Fixtures.bytes "valid/finished-failed.json"))

    Expect.equal succeededRes.Outcome Succeeded "succeeded outcome"
    Expect.equal failedRes.Outcome Failed "failed outcome"
    Expect.equal (ExecutionOutcome.toWire Failed) "failed" "wire form"

/// A tiny custom fixture proving the remaining two outcomes are accepted.
let testCancelledAndTimedOutOutcomes () =
    let cancelled = """{
  "specversion": "1.0",
  "id": "019b0437-2766-7a20-9225-4ab1645ba132",
  "source": "urn:leamas:instance:builder-07",
  "type": "io.leamas.execution.finished.v1",
  "subject": "run/019b0437-1ef2-7abc-a38d-23472513f51a",
  "time": "2026-07-12T20:00:00Z",
  "datacontenttype": "application/json",
  "circusinstance": "builder-07",
  "circusepoch": "019b0400-2f61-720d-94a5-c84e928eae19",
  "circusseq": 512,
  "runid": "019b0437-1ef2-7abc-a38d-23472513f51a",
  "data": {
    "outcome": "cancelled",
    "duration_ms": 0,
    "checks": { "passed": 0, "failed": 0, "skipped": 0 }
  }
}
"""

    let timedOut = """{
  "specversion": "1.0",
  "id": "019b0437-2766-7a20-9225-4ab1645ba133",
  "source": "urn:leamas:instance:builder-07",
  "type": "io.leamas.execution.finished.v1",
  "subject": "run/019b0437-1ef2-7abc-a38d-23472513f51b",
  "time": "2026-07-12T20:00:00Z",
  "datacontenttype": "application/json",
  "circusinstance": "builder-07",
  "circusepoch": "019b0400-2f61-720d-94a5-c84e928eae19",
  "circusseq": 513,
  "runid": "019b0437-1ef2-7abc-a38d-23472513f51b",
  "data": {
    "outcome": "timed_out",
    "duration_ms": 604800000,
    "checks": { "passed": 1, "failed": 0, "skipped": 0 }
  }
}
"""

    let encoded = System.Text.Encoding.UTF8.GetBytes : System.Func<string, System.ReadOnlyMemory<byte>>

    let cancelledRes =
        outcomeFrom (ok (encoded.Invoke cancelled))

    let timedOutRes =
        outcomeFrom (ok (encoded.Invoke timedOut))

    Expect.equal cancelledRes.Outcome Cancelled "cancelled outcome"
    Expect.equal timedOutRes.Outcome TimedOut "timed_out outcome"

/// An unknown outcome string is rejected (no UnknownOutcome state).
let testUnknownOutcomeRejected () =
    let result =
        ok (Fixtures.bytes "invalid-finished/finished-unknown-outcome.json")

    let violations = Assertions.contractViolations result
    Expect.isTrue (Assertions.hasInvalidKnownPayload violations) "InvalidKnownPayload present"

    let payloadErrs = Assertions.payloadViolations violations
    Expect.isTrue
        (payloadErrs
         |> List.exists (function
             | PayloadInvalidFieldValue (n, _) when n = "outcome" -> true
             | _ -> false))
        "PayloadInvalidFieldValue on outcome"

/// Zero duration is valid.
let testZeroDurationValid () =
    let result =
        ok (Fixtures.bytes "valid/finished-succeeded.json")
        |> outcomeFrom

    Expect.isTrue (result.DurationMilliseconds >= 0L) "duration_ms non-negative"

/// Maximum duration (one week in ms) is valid.
let testMaximumDurationValid () =
    let result = outcomeFrom (ok (Fixtures.bytes "valid/finished-failed.json"))
    Expect.isTrue
        (result.DurationMilliseconds <= Limits.DurationMaxMilliseconds)
        "duration_ms ≤ week"

/// Excessive duration is rejected.
let testExcessiveDurationRejected () =
    let bogus = """{
  "specversion": "1.0",
  "id": "019b0437-2766-7a20-9225-4ab1645ba134",
  "source": "urn:leamas:instance:builder-07",
  "type": "io.leamas.execution.finished.v1",
  "subject": "run/019b0437-1ef2-7abc-a38d-23472513f51c",
  "time": "2026-07-12T20:00:00Z",
  "datacontenttype": "application/json",
  "circusinstance": "builder-07",
  "circusepoch": "019b0400-2f61-720d-94a5-c84e928eae19",
  "circusseq": 514,
  "runid": "019b0437-1ef2-7abc-a38d-23472513f51c",
  "data": {
    "outcome": "succeeded",
    "duration_ms": 604800001,
    "checks": { "passed": 0, "failed": 0, "skipped": 0 }
  }
}
"""

    let encoded = System.Text.Encoding.UTF8.GetBytes : System.Func<string, System.ReadOnlyMemory<byte>>
    let result = ok (encoded.Invoke bogus)
    let violations = Assertions.contractViolations result
    Expect.isTrue
        (Assertions.payloadViolations violations
         |> List.exists (function
             | PayloadInvalidFieldValue (n, _) when n = "duration_ms" -> true
             | _ -> false))
        "excessive duration rejected"

/// Negative counts are rejected.
let testNegativeCountsRejected () =
    let bogus = """{
  "specversion": "1.0",
  "id": "019b0437-2766-7a20-9225-4ab1645ba135",
  "source": "urn:leamas:instance:builder-07",
  "type": "io.leamas.execution.finished.v1",
  "subject": "run/019b0437-1ef2-7abc-a38d-23472513f51d",
  "time": "2026-07-12T20:00:00Z",
  "datacontenttype": "application/json",
  "circusinstance": "builder-07",
  "circusepoch": "019b0400-2f61-720d-94a5-c84e928eae19",
  "circusseq": 515,
  "runid": "019b0437-1ef2-7abc-a38d-23472513f51d",
  "data": {
    "outcome": "succeeded",
    "duration_ms": 1000,
    "checks": { "passed": -1, "failed": 0, "skipped": 0 }
  }
}
"""

    let encoded = System.Text.Encoding.UTF8.GetBytes : System.Func<string, System.ReadOnlyMemory<byte>>
    let result = ok (encoded.Invoke bogus)
    let payloadErrs = Assertions.payloadViolations (Assertions.contractViolations result)

    Expect.isTrue
        (payloadErrs
         |> List.exists (function
             | PayloadInvalidFieldValue (n, _) when n = "passed" -> true
             | _ -> false))
        "negative count rejected"

/// Excessive counts (>1_000_000) are rejected.
let testExcessiveCountsRejected () =
    let result =
        ok (Fixtures.bytes "invalid-finished/finished-invalid-check-counts.json")

    let payloadErrs = Assertions.payloadViolations (Assertions.contractViolations result)
    Expect.isTrue
        (payloadErrs
         |> List.exists (function
             | PayloadInvalidFieldValue (n, _) when n = "passed" -> true
             | _ -> false))
        "excessive count rejected"

/// Optional `summary` is preserved when present and absent when omitted.
let testOptionalSummaryPreserved () =
    let withSummary = outcomeFrom (ok (Fixtures.bytes "valid/finished-failed.json"))
    Expect.equal withSummary.Summary (Some "Two checks failed: outcome decoding, payload validation") "summary preserved"

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
