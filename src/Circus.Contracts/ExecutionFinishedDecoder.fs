namespace Circus.Contracts

open System
open System.Text.Json
open Circus.Domain

module internal FinishedPayload =
    open Primitives

    /// Lower-case canonical names and the recognised event-type string for
    /// the `io.leamas.execution.finished.v1` payload.
    module internal FieldNames =
        let [<Literal>] EventType = "io.leamas.execution.finished.v1"
        let [<Literal>] Outcome = "outcome"
        let [<Literal>] DurationMs = "duration_ms"
        let [<Literal>] Summary = "summary"
        let [<Literal>] Checks = "checks"
        let [<Literal>] Passed = "passed"
        let [<Literal>] Failed = "failed"
        let [<Literal>] Skipped = "skipped"

    /// Wire-format limits for the finished payload fields.
    module internal Limits =
        let ChecksMaxCount = 1_000_000

    /// Decode the `outcome` field. Rejects unknown outcome strings
    /// rather than mapping them to an "Unknown" outcome state.
    let private readOutcome (root: JsonElement) : PayloadResult<ExecutionOutcome> =
        let mutable el = Unchecked.defaultof<JsonElement>

        match root.TryGetProperty(FieldNames.Outcome, &el) with
        | false -> Error(NonEmptyList.singleton(PayloadMissingField FieldNames.Outcome))
        | true ->
            match el.ValueKind with
            | JsonValueKind.String ->
                let text = el.GetString()

                match ExecutionOutcome.tryFromWire text with
                | Some v -> Ok v
                | None ->
                    Error(
                        NonEmptyList.singleton(
                            PayloadInvalidFieldValue(
                                FieldNames.Outcome,
                                "must be one of: succeeded, failed, cancelled, timed_out"
                            )
                        )
                    )
            | _ -> Error(NonEmptyList.singleton(PayloadInvalidFieldType(FieldNames.Outcome, "string")))

    /// Decode the `duration_ms` field as a 64-bit integer in
    /// `0..Primitives.DurationMaxMilliseconds`.
    let private readDuration (root: JsonElement) : PayloadResult<int64> =
        let mutable el = Unchecked.defaultof<JsonElement>

        match root.TryGetProperty(FieldNames.DurationMs, &el) with
        | false -> Error(NonEmptyList.singleton(PayloadMissingField FieldNames.DurationMs))
        | true ->
            match el.ValueKind with
            | JsonValueKind.Number ->
                let mutable value = 0L

                if el.TryGetInt64(&value) then
                    if value < 0L then
                        Error(
                            NonEmptyList.singleton(
                                PayloadInvalidFieldValue(FieldNames.DurationMs, "must be >= 0")
                            )
                        )
                    elif value > Primitives.DurationMaxMilliseconds then
                        Error(
                            NonEmptyList.singleton(
                                PayloadInvalidFieldValue(
                                    FieldNames.DurationMs,
                                    sprintf "must not exceed %d (one week in ms)" Primitives.DurationMaxMilliseconds
                                )
                            )
                        )
                    else
                        Ok value
                else
                    Error(
                        NonEmptyList.singleton(
                            PayloadInvalidFieldValue(FieldNames.DurationMs, "must fit in Int64")
                        )
                    )
            | _ ->
                Error(
                    NonEmptyList.singleton(PayloadInvalidFieldType(FieldNames.DurationMs, "integer"))
                )

    /// Decode the optional `summary` string field. Empty strings, null,
    /// and over-length values are validated as typed violations.
    let private readSummary (root: JsonElement) : PayloadResult<string option> =
        let mutable el = Unchecked.defaultof<JsonElement>

        match root.TryGetProperty(FieldNames.Summary, &el) with
        | false -> Ok None
        | true ->
            match el.ValueKind with
            | JsonValueKind.Null -> Ok None
            | JsonValueKind.String ->
                let text = el.GetString()

                if text.Length > Primitives.SummaryMaxLength then
                    Error(
                        NonEmptyList.singleton(
                            PayloadInvalidFieldValue(
                                FieldNames.Summary,
                                sprintf "must not exceed %d characters" Primitives.SummaryMaxLength
                            )
                        )
                    )
                else
                    Ok(Some text)
            | _ -> Error(NonEmptyList.singleton(PayloadInvalidFieldType(FieldNames.Summary, "string or null")))

    /// Decode one `checks.{passed,failed,skipped}` counter.
    let private readCheckCount (label: string) (el: JsonElement) : PayloadResult<int> =
        match el.ValueKind with
        | JsonValueKind.Number ->
            let mutable value = 0

            if el.TryGetInt32(&value) then
                if value < 0 || value > Limits.ChecksMaxCount then
                    Error(
                        NonEmptyList.singleton(
                            PayloadInvalidFieldValue(label, sprintf "must be 0..%d" Limits.ChecksMaxCount)
                        )
                    )
                else
                    Ok value
            else
                Error(NonEmptyList.singleton(PayloadInvalidFieldValue(label, "must fit in Int32")))
        | _ -> Error(NonEmptyList.singleton(PayloadInvalidFieldType(label, "integer")))

    /// Decode the `checks` object independently for each counter:
    /// `passed`, `failed`, `skipped`. Missing counters yield
    /// `PayloadMissingField`; present-but-invalid counters yield
    /// `PayloadInvalidFieldType` / `PayloadInvalidFieldValue`. All errors
    /// are concatenated into a single non-empty list.
    let private readChecks (root: JsonElement) : PayloadResult<CheckCounts> =
        let mutable el = Unchecked.defaultof<JsonElement>

        match root.TryGetProperty(FieldNames.Checks, &el) with
        | false -> Error(NonEmptyList.singleton(PayloadMissingField FieldNames.Checks))
        | true ->
            match el.ValueKind with
            | JsonValueKind.Object ->
                let mutable pEl = Unchecked.defaultof<JsonElement>
                let mutable fEl = Unchecked.defaultof<JsonElement>
                let mutable sEl = Unchecked.defaultof<JsonElement>

                let hasPassed = el.TryGetProperty(FieldNames.Passed, &pEl)
                let hasFailed = el.TryGetProperty(FieldNames.Failed, &fEl)
                let hasSkipped = el.TryGetProperty(FieldNames.Skipped, &sEl)

                let missingViolations =
                    [
                        if not hasPassed then
                            yield PayloadMissingField FieldNames.Passed
                        if not hasFailed then
                            yield PayloadMissingField FieldNames.Failed
                        if not hasSkipped then
                            yield PayloadMissingField FieldNames.Skipped
                    ]

                let validateCounter (label: string) (countEl: JsonElement) : PayloadViolation list =
                    match readCheckCount label countEl with
                    | Ok _ -> []
                    | Error n -> NonEmptyList.toList n

                let countViolations =
                    [ if hasPassed then
                          yield! validateCounter FieldNames.Passed pEl
                      if hasFailed then
                          yield! validateCounter FieldNames.Failed fEl
                      if hasSkipped then
                          yield! validateCounter FieldNames.Skipped sEl ]

                let allErrors = missingViolations @ countViolations

                match allErrors with
                | first :: rest -> Error(NonEmptyList.cons first rest)
                | [] ->
                    let getCount (label: string) (countEl: JsonElement) : int =
                        match readCheckCount label countEl with
                        | Ok v -> v
                        | Error _ -> failwithf "invariant violation: %s succeeded above" label

                    Ok
                        { Passed = getCount FieldNames.Passed pEl
                          Failed = getCount FieldNames.Failed fEl
                          Skipped = getCount FieldNames.Skipped sEl }
            | _ ->
                Error(
                    NonEmptyList.singleton(PayloadInvalidFieldType(FieldNames.Checks, "object"))
                )

    /// Decode an `ExecutionFinished` payload. Independent validation
    /// failures are accumulated.
    let decode (data: JsonElement) (runId: RunId) : ValidationResult<ExecutionFinished> =
        let outcomeRes = readOutcome data
        let durationRes = readDuration data
        let summaryRes = readSummary data
        let checksRes = readChecks data

        let mutable errors : PayloadViolation list = []

        let collect (r: PayloadResult<'v>) =
            match r with
            | Ok _ -> ()
            | Error n -> errors <- NonEmptyList.toList n @ errors

        collect outcomeRes
        collect durationRes
        collect summaryRes
        collect checksRes

        match errors with
        | first :: rest ->
            Error(
                NonEmptyList.singleton(
                    InvalidKnownPayload(FieldNames.EventType, NonEmptyList.cons first rest)
                )
            )
        | [] ->
            let unwrap (label: string) (r: PayloadResult<'v>) : 'v =
                match r with
                | Ok v -> v
                | Error _ -> failwithf "invariant violation: %s succeeded above" label

            Ok
                { RunId = runId
                  Outcome = unwrap FieldNames.Outcome outcomeRes
                  DurationMilliseconds = unwrap FieldNames.DurationMs durationRes
                  Summary = unwrap FieldNames.Summary summaryRes
                  Checks = unwrap FieldNames.Checks checksRes }
