module Circus.Persistence.Postgres.Tests.Support

open System
open System.Text
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

let bodyFor (event: ValidatedEvent) (pretty: bool) =
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

    let compact = prefix + data + "}"

    if pretty then
        compact.Replace(",\"", ",\n\"").Replace("{\"", "{\n\"").Replace("}", "\n}")
    else
        compact

let requestFor (event: ValidatedEvent) (body: string) : IngestEventRequest =
    { Event = event
      RawBody = Encoding.UTF8.GetBytes body
      EnvelopeJson = body }

let requestWithFormatting (event: ValidatedEvent) (pretty: bool) = requestFor event (bodyFor event pretty)
