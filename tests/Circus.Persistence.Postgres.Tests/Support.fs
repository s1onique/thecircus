module Circus.Persistence.Postgres.Tests.Support

open System
open System.Reflection
open System.Text
open Npgsql
open Circus.Application
open Circus.Contracts
open Circus.Domain

let private create value factory = factory value |> Option.get

let source = create "urn:test:producer" EventSource.tryCreate
let instance = create "builder-test" InstanceId.tryCreate

let startedEvent (eventId: string) (runId: Guid) (epoch: Guid) (sequence: int64) : ValidatedEvent =
    let typedRun = create runId RunId.tryCreate

    { EventId = create eventId EventId.tryCreate
      Source = source
      EventType = create "io.leamas.execution.started.v1" EventType.tryCreate
      Subject = sprintf "run/%O" runId
      ObservedAt = DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)
      InstanceId = instance
      EpochId = create epoch EpochId.tryCreate
      Sequence = create sequence EventSequence.tryCreate
      RunId = typedRun
      Extensions = Map.empty
      Event =
        ExecutionStartedEvent
            { RunId = typedRun
              Repository = create "circus-repository" RepositoryRef.tryCreate
              ActId = Some(create "ACT-TEST" ActId.tryCreate)
              LeamasVersion = create "1.0.0" LeamasVersion.tryCreate
              GitRevision = Some "abcdef"
              StartedBy = Some "test" } }

let finishedEvent
    (eventId: string)
    (runId: Guid)
    (epoch: Guid)
    (sequence: int64)
    (outcome: ExecutionOutcome)
    : ValidatedEvent =
    let typedRun = create runId RunId.tryCreate

    { EventId = create eventId EventId.tryCreate
      Source = source
      EventType = create "io.leamas.execution.finished.v1" EventType.tryCreate
      Subject = sprintf "run/%O" runId
      ObservedAt = DateTimeOffset(2026, 7, 15, 12, 1, 0, TimeSpan.Zero)
      InstanceId = instance
      EpochId = create epoch EpochId.tryCreate
      Sequence = create sequence EventSequence.tryCreate
      RunId = typedRun
      Extensions = Map.empty
      Event =
        ExecutionFinishedEvent
            { RunId = typedRun
              Outcome = outcome
              DurationMilliseconds = 1000L
              Summary = Some "finished"
              Checks = { Passed = 3; Failed = 1; Skipped = 0 } } }

let finishedEventWithChecks
    (eventId: string)
    (runId: Guid)
    (epoch: Guid)
    (sequence: int64)
    (outcome: ExecutionOutcome)
    (passed: int)
    (failed: int)
    (skipped: int)
    : ValidatedEvent =
    let typedRun = create runId RunId.tryCreate

    { EventId = create eventId EventId.tryCreate
      Source = source
      EventType = create "io.leamas.execution.finished.v1" EventType.tryCreate
      Subject = sprintf "run/%O" runId
      ObservedAt = DateTimeOffset(2026, 7, 15, 12, 1, 0, TimeSpan.Zero)
      InstanceId = instance
      EpochId = create epoch EpochId.tryCreate
      Sequence = create sequence EventSequence.tryCreate
      RunId = typedRun
      Extensions = Map.empty
      Event =
        ExecutionFinishedEvent
            { RunId = typedRun
              Outcome = outcome
              DurationMilliseconds = 1000L
              Summary = Some "finished"
              Checks =
                { Passed = passed
                  Failed = failed
                  Skipped = skipped } } }

let unknownEvent (eventId: string) (runId: Guid) (epoch: Guid) (sequence: int64) : ValidatedEvent =
    let typedRun = create runId RunId.tryCreate

    { EventId = create eventId EventId.tryCreate
      Source = source
      EventType = create "io.leamas.execution.future.v1" EventType.tryCreate
      Subject = sprintf "run/%O" runId
      ObservedAt = DateTimeOffset(2026, 7, 15, 12, 2, 0, TimeSpan.Zero)
      InstanceId = instance
      EpochId = create epoch EpochId.tryCreate
      Sequence = create sequence EventSequence.tryCreate
      RunId = typedRun
      Extensions = Map.empty
      Event =
        UnrecognizedEvent
            { EventType = "io.leamas.execution.future.v1"
              Data = Some(RawJson.unsafeOfString "{}") } }

/// Build a compact, semantically equivalent JSON body for an event.
let compactBodyFor (event: ValidatedEvent) =
    let runId = RunId.value event.RunId
    let eventId = EventId.value event.EventId
    let epoch = EpochId.value event.EpochId
    let sequence = EventSequence.value event.Sequence

    let prefix =
        sprintf
            "{\"specversion\":\"1.0\",\"id\":\"%s\",\"source\":\"%s\",\"type\":\"%s\",\"subject\":\"run/%O\",\"time\":\"2026-07-15T12:00:00Z\",\"datacontenttype\":\"application/json\",\"circusinstance\":\"%s\",\"circusepoch\":\"%O\",\"circusseq\":%d,\"runid\":\"%O\",\"data\":"
            eventId
            (EventSource.value event.Source)
            (EventType.value event.EventType)
            runId
            (InstanceId.value event.InstanceId)
            epoch
            sequence
            runId

    let data =
        match event.Event with
        | ExecutionStartedEvent started ->
            sprintf
                "{\"repository_ref\":\"%s\",\"act_id\":\"%s\",\"leamas_version\":\"%s\",\"git_revision\":\"%s\",\"started_by\":\"%s\"}"
                (RepositoryRef.value started.Repository)
                (started.ActId |> Option.map ActId.value |> Option.defaultValue "")
                (LeamasVersion.value started.LeamasVersion)
                (started.GitRevision |> Option.defaultValue "")
                (started.StartedBy |> Option.defaultValue "")
        | ExecutionFinishedEvent finished ->
            sprintf
                "{\"outcome\":\"%s\",\"duration_ms\":%d,\"summary\":\"%s\",\"checks\":{\"passed\":%d,\"failed\":%d,\"skipped\":%d}}"
                (ExecutionOutcome.toWire finished.Outcome)
                finished.DurationMilliseconds
                (finished.Summary |> Option.defaultValue "")
                finished.Checks.Passed
                finished.Checks.Failed
                finished.Checks.Skipped
        | UnrecognizedEvent _ -> "{}"

    prefix + data + "}"

/// Different insignificant whitespace, otherwise the same JSON value.
let prettyBodyFor (event: ValidatedEvent) =
    (compactBodyFor event).Replace(",\"", ",\n\"").Replace("{\"", "{\n\"").Replace("}", "\n}")

/// Reorder top-level object keys into a different but stable order.
let reorderedTopBodyFor (event: ValidatedEvent) =
    let body = compactBodyFor event
    // The compact form starts with the specversion/id/source/type prefix;
    // reordering swaps id<->source positions in the canonical key list to
    // produce a different on-the-wire property order.
    let idIdx = body.IndexOf "\"id\":\""
    let sourceIdx = body.IndexOf "\"source\":\""

    if idIdx < 0 || sourceIdx < 0 then
        body
    else
        let idValStart = idIdx + 6
        let idValEnd = body.IndexOf("\"", idValStart)
        let idValue = body.Substring(idValStart, idValEnd - idValStart)

        let sourceValStart = sourceIdx + 9
        let sourceValEnd = body.IndexOf("\"", sourceValStart)
        let sourceValue = body.Substring(sourceValStart, sourceValEnd - sourceValStart)

        // Replace the original order (id then source) with (source then id).
        let replaced =
            body
                .Replace($"\"id\":\"{idValue}\"", $"__ID__")
                .Replace($"\"source\":\"{sourceValue}\"", $"\"id\":\"{idValue}\"")
                .Replace("__ID__", $"\"source\":\"{sourceValue}\"")

        replaced

/// Reorder nested data properties (repository_ref <-> leamas_version).
let reorderedDataBodyFor (event: ValidatedEvent) =
    match event.Event with
    | ExecutionStartedEvent started ->
        let runId = RunId.value event.RunId
        let eventId = EventId.value event.EventId
        let epoch = EpochId.value event.EpochId
        let sequence = EventSequence.value event.Sequence

        let prefix =
            sprintf
                "{\"specversion\":\"1.0\",\"id\":\"%s\",\"source\":\"%s\",\"type\":\"%s\",\"subject\":\"run/%O\",\"time\":\"2026-07-15T12:00:00Z\",\"datacontenttype\":\"application/json\",\"circusinstance\":\"%s\",\"circusepoch\":\"%O\",\"circusseq\":%d,\"runid\":\"%O\",\"data\":"
                eventId
                (EventSource.value event.Source)
                (EventType.value event.EventType)
                runId
                (InstanceId.value event.InstanceId)
                epoch
                sequence
                runId

        let data =
            sprintf
                "{\"leamas_version\":\"%s\",\"repository_ref\":\"%s\",\"act_id\":\"%s\",\"git_revision\":\"%s\",\"started_by\":\"%s\"}"
                (LeamasVersion.value started.LeamasVersion)
                (RepositoryRef.value started.Repository)
                (started.ActId |> Option.map ActId.value |> Option.defaultValue "")
                (started.GitRevision |> Option.defaultValue "")
                (started.StartedBy |> Option.defaultValue "")

        prefix + data + "}"
    | _ -> compactBodyFor event

/// Reorder nested checks properties (skipped <-> passed) for a finished event.
let reorderedChecksBodyFor (event: ValidatedEvent) =
    match event.Event with
    | ExecutionFinishedEvent finished ->
        let runId = RunId.value event.RunId
        let eventId = EventId.value event.EventId
        let epoch = EpochId.value event.EpochId
        let sequence = EventSequence.value event.Sequence

        let prefix =
            sprintf
                "{\"specversion\":\"1.0\",\"id\":\"%s\",\"source\":\"%s\",\"type\":\"%s\",\"subject\":\"run/%O\",\"time\":\"2026-07-15T12:00:00Z\",\"datacontenttype\":\"application/json\",\"circusinstance\":\"%s\",\"circusepoch\":\"%O\",\"circusseq\":%d,\"runid\":\"%O\",\"data\":"
                eventId
                (EventSource.value event.Source)
                (EventType.value event.EventType)
                runId
                (InstanceId.value event.InstanceId)
                epoch
                sequence
                runId

        let data =
            sprintf
                "{\"outcome\":\"%s\",\"duration_ms\":%d,\"summary\":\"%s\",\"checks\":{\"skipped\":%d,\"passed\":%d,\"failed\":%d}}"
                (ExecutionOutcome.toWire finished.Outcome)
                finished.DurationMilliseconds
                (finished.Summary |> Option.defaultValue "")
                finished.Checks.Skipped
                finished.Checks.Passed
                finished.Checks.Failed

        prefix + data + "}"
    | _ -> compactBodyFor event

let requestFor (event: ValidatedEvent) (body: string) : IngestEventRequest =
    { Event = event
      RawBody = Encoding.UTF8.GetBytes body
      EnvelopeJson = body }

/// Build a request whose raw bytes are the compact original.  Used by every
/// "first call" branch of the semantic replay tests.
let compactRequest (event: ValidatedEvent) : IngestEventRequest = requestFor event (compactBodyFor event)

/// Build a request whose raw bytes differ from the compact original by
/// insignificant whitespace only.
let prettyRequest (event: ValidatedEvent) : IngestEventRequest = requestFor event (prettyBodyFor event)

/// Build a request whose raw bytes differ from the compact original by
/// reordering the top-level object properties.
let reorderedTopRequest (event: ValidatedEvent) : IngestEventRequest =
    requestFor event (reorderedTopBodyFor event)

/// Build a request whose raw bytes differ from the compact original by
/// reordering the nested `data` properties.
let reorderedDataRequest (event: ValidatedEvent) : IngestEventRequest =
    requestFor event (reorderedDataBodyFor event)

/// Build a request whose raw bytes differ from the compact original by
/// reordering the nested `checks` properties for a finished event.
let reorderedChecksRequest (event: ValidatedEvent) : IngestEventRequest =
    requestFor event (reorderedChecksBodyFor event)

/// Backwards-compatible alias used by tests written before the per-variant
/// builders above.  Identical to `compactRequest` when `pretty = false`.
let requestWithFormatting (event: ValidatedEvent) (pretty: bool) =
    if pretty then prettyRequest event else compactRequest event

/// Construct a `PostgresException` carrying the supplied SQLSTATE.  Used
/// by retry-composition tests to drive the production retry path against
/// SQLSTATE 40001 and 40P01 without standing up a real serialization
/// conflict.  `PostgresException` is the public type that exposes a
/// non-default SQLSTATE through its constructor, so the production
/// `isRetryable` classifier (`ex.SqlState = "40001"` /
/// `ex.SqlState = "40P01"`) sees the intended code.
let faultyNpgsqlException (sqlState: string) : PostgresException =
    PostgresException("simulated retryable failure (" + sqlState + ")", "ERROR", "ERROR", sqlState)
