module Circus.Persistence.Postgres.Tests.ProjectionIntegrationTests

open System
open Expecto
open Circus.Application
open Circus.Domain
open Circus.Persistence.Postgres
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.Support

let private ingest (fixture: PostgresFixture) event pretty =
    fixture.Ingestion.Ingest(requestWithFormatting event pretty) |> wait

let private unwrap result =
    match result with
    | Ok value -> value
    | Error failure -> failwithf "Unexpected result error: %A" failure

let private getProjection (fixture: PostgresFixture) (runId: RunId) =
    match fixture.ProjectionRepo.GetByRunId runId |> wait with
    | Ok value -> value
    | Error failure -> failwithf "Projection read failed: %A" failure

let tests (fixture: PostgresFixture) =
    testList
        "Incremental projection equals journal rebuild"
        [ test "started then finished is equal to rebuild" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()
              let started = startedEvent "rebuild-started" runId epoch 1L

              let finished =
                  finishedEvent "rebuild-finished" runId epoch 2L ExecutionOutcome.Succeeded

              ingest fixture started false |> ignore
              ingest fixture finished false |> ignore
              let incremental = getProjection fixture started.RunId |> Option.get

              let rebuilt =
                  ProjectionRebuild.rebuildFromJournal fixture.DataSource
                  |> wait
                  |> unwrap
                  |> Map.find incremental.RunId

              Expect.equal incremental rebuilt "Complete projection equals reducer rebuild"
          }

          test "finished then started is complete and rebuild-equivalent" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()

              let finished =
                  finishedEvent "rebuild-finished-first" runId epoch 1L ExecutionOutcome.Failed

              let started = startedEvent "rebuild-started-second" runId epoch 2L
              ingest fixture finished false |> ignore
              ingest fixture started false |> ignore
              let incremental = getProjection fixture finished.RunId |> Option.get
              Expect.equal incremental.State Completed "Finished-first then started completes"

              let rebuilt =
                  ProjectionRebuild.rebuildFromJournal fixture.DataSource
                  |> wait
                  |> unwrap
                  |> Map.find incremental.RunId

              Expect.equal incremental rebuilt "Order is preserved by both paths"
          }

          test "conflict is monotonic and first authority survives rebuild" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()
              let first = startedEvent "conflict-first" runId epoch 1L
              let second = startedEvent "conflict-second" runId epoch 2L
              ingest fixture first false |> ignore
              ingest fixture second false |> ignore
              let incremental = getProjection fixture first.RunId |> Option.get
              Expect.equal incremental.State Conflicted "Conflict state"
              Expect.equal incremental.StartedEvent (Some(JournalPosition 1L)) "First started authority preserved"
              Expect.equal incremental.Version 2L "Conflict mutation increments once"

              let rebuilt =
                  ProjectionRebuild.rebuildFromJournal fixture.DataSource
                  |> wait
                  |> unwrap
                  |> Map.find incremental.RunId

              Expect.equal rebuilt.State Conflicted "Rebuild remains conflicted"
              Expect.equal rebuilt.StartedEvent incremental.StartedEvent "Rebuild preserves first authority"
              Expect.equal rebuilt incremental "Complete semantic equality"
          }

          test "unknown event is durable but ignored by incremental and rebuild reducers" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let epoch = Guid.NewGuid()
              let started = startedEvent "unknown-start" runId epoch 1L
              let unknown = unknownEvent "unknown-kind" runId epoch 2L
              ingest fixture started false |> ignore
              ingest fixture unknown false |> ignore
              let incremental = getProjection fixture started.RunId |> Option.get

              let rebuilt =
                  ProjectionRebuild.rebuildFromJournal fixture.DataSource
                  |> wait
                  |> unwrap
                  |> Map.find incremental.RunId

              Expect.equal incremental.State StartedOnly "Unknown does not mutate state"
              Expect.equal incremental.Version 1L "Unknown does not increment version"
              Expect.equal incremental rebuilt "Unknown is ignored by both paths"
          }

          test "replay does not create a second projection version" {
              fixture.Reset()
              let runId = Guid.NewGuid()
              let event = startedEvent "projection-replay" runId (Guid.NewGuid()) 1L

              match ingest fixture event false, ingest fixture event true with
              | Success(Inserted _, Some first), Success(IdempotentReplay _, None) ->
                  let current = getProjection fixture event.RunId |> Option.get
                  Expect.equal current first "Replay leaves projection unchanged"
                  Expect.equal current.Version 1L "Replay leaves version at one"
              | firstResult, secondResult -> failwithf "Expected insert/replay, got %A and %A" firstResult secondResult
          } ]
