# Leamas Events Contract v1

This document defines **leamas-events-v1**, the first versioned contract through which Leamas instances report execution events to The Circus.

The contract is implemented as a pure, deterministic F# decoder under [`src/Circus.Contracts/`](../../src/Circus.Contracts). It accepts CloudEvents-structured JSON envelopes, validates Circus-specific extensions, and dispatches onto known or unknown event payload types.

> **Scope.** This contract is the reading half of the Leamas ↔ Circus wire boundary. It defines decoding and validation only. The persistence and HTTP endpoints belong to `ACT-CIRCUS-INGESTION-JOURNAL01` and beyond.

---

## 1. Envelope

The wire representation is the CloudEvents **structured JSON** binding:

```
Content-Type: application/cloudevents+json
```

A valid `leamas-events-v1` envelope is a single JSON object containing every property listed below. Property order does not affect decoding.

### 1.1 Required CloudEvents attributes

| Property           | Type            | Rule                                                              |
| ------------------ | --------------- | ---------------------------------------------------------------- |
| `specversion`      | string          | exactly `"1.0"`                                                  |
| `id`               | string          | non-empty, ≤ 128 characters                                      |
| `source`           | string (URI)    | non-empty URI reference, ≤ 512 characters                        |
| `type`             | string          | non-empty, ≤ 255 characters; recognised values listed in §3      |
| `subject`          | string          | exactly `run/<runid>` where `<runid>` is the textual `runid` value |
| `time`             | string          | ISO-8601 timestamp with explicit offset (Z or `+HH:MM`/`-HH:MM`) |
| `datacontenttype`  | string          | exactly `application/json`                                        |
| `data`             | object          | JSON object; payload specific to `type` (§3)                     |

### 1.2 Required Circus extensions

| Property           | Type            | Rule                                                              |
| ------------------ | --------------- | ---------------------------------------------------------------- |
| `circusinstance`   | string          | non-empty, ≤ 128 characters                                      |
| `circusepoch`      | string (UUID)   | non-empty UUID                                                   |
| `circusseq`        | integer         | `0 .. Int64.MaxValue`                                            |
| `runid`            | string (UUID)   | non-empty UUID; must match the `subject` suffix                   |

> Extension names are required to be lowercase. They may contain lowercase letters, digits, and `_` only.

### 1.3 Example envelope

```json
{
  "specversion": "1.0",
  "id": "019b0437-2766-7a20-9225-4ab1645ba115",
  "source": "urn:leamas:instance:builder-07",
  "type": "io.leamas.execution.started.v1",
  "subject": "run/019b0437-1ef2-7abc-a38d-23472513f509",
  "time": "2026-07-12T20:00:00Z",
  "datacontenttype": "application/json",

  "circusinstance": "builder-07",
  "circusepoch": "019b0400-2f61-720d-94a5-c84e928eae19",
  "circusseq": 418,
  "runid": "019b0437-1ef2-7abc-a38d-23472513f509",

  "data": {
    "repository_ref": "k9b",
    "act_id": "ACT-K9B-EXAMPLE01",
    "leamas_version": "0.1.0",
    "git_revision": "0123456789abcdef",
    "started_by": "alex"
  }
}
```

---

## 2. Forward compatibility

Unknown `type` values, unknown extension names with valid shape, and any other JSON properties beyond those listed in §1 are preserved verbatim and surfaced through the `ValidatedEvent.Extensions` map (extensions) or the `UnrecognizedEvent` case (event type and data).

> **Forward compatibility is mandatory.** Producers may deploy new event types or new extensions before consumers upgrade. The decoder must never reject a structurally valid envelope merely because a future valid field is unknown.

### 2.1 Extension preservation

Valid unknown extensions are preserved in `ValidatedEvent.Extensions`, keyed by extension name with the JSON-serialised value. Invalid extension names are rejected with `InvalidExtensionName`; valid names with invalid value shapes are rejected with `InvalidFieldValue` or `InvalidFieldType`.

### 2.2 Unknown event types

When `type` is not one of the recognised values listed in §3, the envelope is decoded and surfaced as `UnrecognizedEvent`:

| Property | Shape                                           |
| -------- | ----------------------------------------------- |
| `EventType` | the verbatim wire `type` string              |
| `Data`      | `RawJson option` for the verbatim `data` JSON |

The decoder never rewrites a structurally valid envelope into `UnrecognizedEvent` solely because a recognised event type has a malformed payload.

---

## 3. Recognised event types

### 3.1 `io.leamas.execution.started.v1`

Emitted by Leamas when an execution is scheduled or starts.

Payload:

| Field            | Type      | Rule                                  |
| ---------------- | --------- | ------------------------------------- |
| `repository_ref` | string    | required, 1–256 characters           |
| `act_id`         | string    | optional, 1–256 characters           |
| `leamas_version` | string    | required, 1–128 characters           |
| `git_revision`   | string    | optional, max 128 characters          |
| `started_by`     | string    | optional, max 128 characters          |

Example:

```json
{
  "repository_ref": "k9b",
  "act_id": "ACT-K9B-EXAMPLE01",
  "leamas_version": "0.1.0",
  "git_revision": "0123456789abcdef",
  "started_by": "alex"
}
```

### 3.2 `io.leamas.execution.finished.v1`

Emitted by Leamas when an execution completes.

Payload:

| Field          | Type    | Rule                                                            |
| -------------- | ------- | --------------------------------------------------------------- |
| `outcome`      | string  | one of `succeeded`, `failed`, `cancelled`, `timed_out`          |
| `duration_ms`  | integer | `0 .. 604_800_000` (one week in ms)                             |
| `summary`      | string  | optional, max 4096 characters                                   |
| `checks`       | object  | required; see below                                             |

`checks`:

| Field      | Type    | Rule                              |
| ---------- | ------- | --------------------------------- |
| `passed`   | integer | `0 .. 1_000_000`                  |
| `failed`   | integer | `0 .. 1_000_000`                  |
| `skipped`  | integer | `0 .. 1_000_000`                  |

Example:

```json
{
  "outcome": "succeeded",
  "duration_ms": 18342,
  "summary": "ACT-local gate passed",
  "checks": {
    "passed": 78,
    "failed": 0,
    "skipped": 0
  }
}
```

### 3.3 Outcome enum

`outcome` admits exactly four values:

- `succeeded`
- `failed`
- `cancelled`
- `timed_out`

Unknown outcome strings **reject** the envelope with `InvalidKnownPayload`. The validated domain does not include an `UnknownOutcome` state because the contract is closed and the producer is expected to upgrade before publishing new outcomes.

### 3.4 Unknown event types

Any other `type` is preserved through `UnrecognizedEvent` with its `EventType` and `Data` JSON preserved. The full envelope metadata (event ID, source, sequencing, extensions, ordering) is still validated and surfaced through `ValidatedEvent` so consumers can audit them.

---

## 4. Limits

The contract enforces a single hard byte bound before parsing:

| Constant                   | Default value | Notes                                  |
| -------------------------- | ------------- | -------------------------------------- |
| `DefaultMaximumBytes`      | `262144`      | 256 KiB; configurable per call         |
| `MalformedJsonMessageLimit`| `200` characters | Diagnostic truncation limit       |
| `SummaryMaxLength`         | `4096` characters | Optional `summary` length bound   |
| `DurationMaxMilliseconds`  | `604_800_000` | One week in milliseconds (duration)    |
| `ChecksMaxCount`           | `1_000_000`   | Each `checks.*` counter                |

A payload larger than `maximumBytes` returns `BodyTooLarge`. The decoder never parses oversized bodies.

---

## 5. Validation semantics

The contract decoder returns `ValidationResult<'v>`:

```fsharp
type ValidationResult<'value> =
    Result<'value, NonEmptyList<ContractViolation>>

type ContractViolation =
    | BodyTooLarge of Maximum: int * Actual: int
    | MalformedJson of BoundedMessage: string
    | RootMustBeObject
    | MissingField of FieldName: string
    | InvalidFieldType of FieldName: string * Expected: string
    | InvalidFieldValue of FieldName: string * Reason: string
    | UnsupportedSpecVersion of string
    | SubjectRunIdMismatch
    | DuplicateField of FieldName: string
    | InvalidExtensionName of Name: string
    | InvalidKnownPayload of EventType: string * Violations: NonEmptyList<PayloadViolation>
```

### 5.1 Rules

1. **Independent violations accumulate.** A missing subject, a missing `data`, and an oversized `data` field may all be reported together. Consumers should not stop on the first violation they observe.
2. **Expected input failures never throw.** Malformed JSON, invalid types, and out-of-range values are typed violations, not exceptions.
3. **Diagnostics are bounded.** No stack traces, no full submitted bodies, no extension values are echoed.
4. **`Subject` ↔ `runid` integrity.** `subject` must be exactly `run/<runid>`, where `<runid>` is the textual form of the top-level `runid`.
5. **Offset-bearing timestamps.** Naive ISO-8601 timestamps (e.g. `2026-07-12T20:00:00`) are rejected; only timestamps with an explicit offset (`Z`, `+HH:MM`, `-HH:MM`) are accepted.
6. **Closed `outcome` set.** Unknown outcome strings yield `InvalidKnownPayload` rather than a domain-level `Unknown` outcome.
7. **Extension names are lowercase.** Names containing uppercase letters or punctuation are rejected with `InvalidExtensionName`.
8. **Reserved attribute set.** The Circus contract recognises a fixed set of envelope attributes. Any other top-level property is treated as a CloudEvents extension.

### 5.2 Known payload violations

`InvalidKnownPayload` carries a `NonEmptyList<PayloadViolation>` so consumers can correct several payload fields per round-trip without parsing strings.

```fsharp
type PayloadViolation =
    | PayloadMissingField of FieldName: string
    | PayloadInvalidFieldType of FieldName: string * Expected: string
    | PayloadInvalidFieldValue of FieldName: string * Reason: string
```

---

## 6. Public API

The Circus binding exposes a single decoder entry point and a default byte limit:

```fsharp
module Circus.Contracts.EventDecoder =
    val DefaultMaximumBytes : int

    val decode : maximumBytes:int -> payload:ReadOnlyMemory<byte> ->
        ValidationResult<ValidatedEvent>
```

`ValidatedEvent` carries:

```fsharp
type ValidatedEvent =
    {
        EventId: EventId
        Source: EventSource
        EventType: EventType
        Subject: string
        ObservedAt: DateTimeOffset
        InstanceId: InstanceId
        EpochId: EpochId
        Sequence: EventSequence
        RunId: RunId
        Extensions: Map<string, RawJson>
        Event: ExecutionEvent
    }
```

`ExecutionEvent` is one of:

| Case                       | Shape                                                                      |
| -------------------------- | -------------------------------------------------------------------------- |
| `ExecutionStartedEvent`   | `ExecutionStarted` (mandatory `runid`, `repository`, `leamas_version`; optional `act_id`, `git_revision`, `started_by`) |
| `ExecutionFinishedEvent`  | `ExecutionFinished` (`runid`, `outcome`, `duration_ms`, optional `summary`, `checks`) |
| `UnrecognizedEvent`       | `UnrecognizedExecutionEvent { EventType: string; Data: RawJson option }`    |

`RawJson` is opaque but JSON-valid text. It does **not** borrow from a disposed `JsonDocument`; the decoder emits plain F# strings so producers or consumers can serialise, hash, or persist them.

---

## 7. Examples

### 7.1 Successful started event

Input:

```json
{
  "specversion": "1.0",
  "id": "019b0437-2766-7a20-9225-4ab1645ba115",
  "source": "urn:leamas:instance:builder-07",
  "type": "io.leamas.execution.started.v1",
  "subject": "run/019b0437-1ef2-7abc-a38d-23472513f509",
  "time": "2026-07-12T20:00:00Z",
  "datacontenttype": "application/json",
  "circusinstance": "builder-07",
  "circusepoch": "019b0400-2f61-720d-94a5-c84e928eae19",
  "circusseq": 418,
  "runid": "019b0437-1ef2-7abc-a38d-23472513f509",
  "data": {
    "repository_ref": "k9b",
    "leamas_version": "0.1.0"
  }
}
```

Result: `Ok (ValidatedEvent)` with `Event = ExecutionStartedEvent { … }`.

### 7.2 Unknown event type

A `type` of `io.leamas.execution.artefact.published.v3` decodes successfully, surfacing as `UnrecognizedEvent { EventType = "io.leamas.execution.artefact.published.v3"; Data = Some "{\"artifact_kind\":\"tarball\",…}" }`.

### 7.3 Subject/runid disagreement

A subject `run/00000000-0000-0000-0000-000000000000` paired with a different `runid` returns `Error ([SubjectRunIdMismatch])`.

### 7.4 Independent envelope failures

An envelope missing `type`, `subject`, and `datacontenttype` returns three independent `MissingField` violations in a single `NonEmptyList`.

---

## 8. Deferred HTTP and persistence behaviour

This contract does **not**:

- expose a `POST /api/v1/events` endpoint;
- authenticate producers via bearer tokens;
- persist events in PostgreSQL;
- validate idempotency keys;
- project runs into derived state.

The above concerns belong to `ACT-CIRCUS-INGESTION-JOURNAL01` and beyond. Until then, the decoder is pure: it produces `ValidatedEvent`s without side effects, and rejects `EventId` collisions or sequence-conflict scenarios at a higher level.

---

## 9. Conformance

Conformance is verified by the `Circus.Contracts.Tests` suite under [`tests/Circus.Contracts.Tests/`](../../tests/Circus.Contracts.Tests). The suite exercises every fixture committed to [`tests/fixtures/events/`](../../tests/fixtures/events/) and proves:

- independent violations accumulate;
- `data` is a required JSON object;
- recognized `type` values dispatch into typed records;
- unknown `type` values become `UnrecognizedEvent`;
- extension names follow the lowercase convention;
- the byte bound is enforced before JSON parsing.

See the closure report for ACT-CIRCUS-INGESTION-CONTRACT01 for the full verification transcript.
