module Circus.Application.Tests.ProjectionDecodingTests

open System
open System.Data
open Expecto
open Circus.Application
open Circus.Persistence.Postgres
open Circus.Domain

let private runId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
let private timestamp = DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc)

let private makeTable () =
    let table = new DataTable()

    [ ("run_id", typeof<Guid>)
      ("state", typeof<string>)
      ("started_journal_position", typeof<int64>)
      ("finished_journal_position", typeof<int64>)
      ("repository_ref", typeof<string>)
      ("act_id", typeof<string>)
      ("leamas_version", typeof<string>)
      ("git_revision", typeof<string>)
      ("started_by", typeof<string>)
      ("started_at", typeof<DateTime>)
      ("outcome", typeof<string>)
      ("finished_at", typeof<DateTime>)
      ("duration_ms", typeof<int64>)
      ("summary", typeof<string>)
      ("checks_passed", typeof<int32>)
      ("checks_failed", typeof<int32>)
      ("checks_skipped", typeof<int32>)
      ("first_journal_position", typeof<int64>)
      ("last_journal_position", typeof<int64>)
      ("conflict_count", typeof<int32>)
      ("version", typeof<int64>) ]
    |> List.iter (fun (name, kind) -> table.Columns.Add(name, kind) |> ignore)

    let row = table.NewRow()
    let set (name: string) (value: obj) = row[name] <- value
    let none = DBNull.Value :> obj
    set "run_id" (box runId)
    set "state" (box "StartedOnly")
    set "started_journal_position" (box 1L)
    set "finished_journal_position" none
    set "repository_ref" (box "repository")
    set "act_id" (box "ACT-1")
    set "leamas_version" (box "1.0.0")
    set "git_revision" (box "abc")
    set "started_by" (box "producer")
    set "started_at" (box timestamp)
    set "outcome" none
    set "finished_at" none
    set "duration_ms" none
    set "summary" none
    set "checks_passed" none
    set "checks_failed" none
    set "checks_skipped" none
    set "first_journal_position" (box 1L)
    set "last_journal_position" (box 1L)
    set "conflict_count" (box 0)
    set "version" (box 1L)
    table.Rows.Add(row)
    table

let private decode (table: DataTable) =
    use reader = table.CreateDataReader()
    reader.Read() |> ignore
    ProjectionRepository.mapToProjection reader

let private expectInvariant label result =
    Expect.equal result (Error ProjectionInvariantFailed) label

let tests =
    testList
        "Strict projection decoding"
        [ test "valid started projection decodes without fabrication" {
              match decode (makeTable ()) with
              | Ok projection ->
                  Expect.equal projection.Version 1L "Version"
                  Expect.equal projection.State StartedOnly "State"
                  Expect.isSome projection.Repository "Repository authority"
              | Error failure -> failwithf "Expected valid projection: %A" failure
          }

          test "database null in required column is an invariant failure" {
              let table = makeTable ()
              table.Rows[0]["run_id"] <- DBNull.Value
              expectInvariant "Required run id null" (decode table)
          }

          test "missing required column is an invariant failure" {
              let table = makeTable ()
              table.Columns.Remove("version")
              expectInvariant "Missing version" (decode table)
          }

          test "incompatible database type is an invariant failure" {
              let table = makeTable ()
              table.Columns.Remove("version")
              table.Columns.Add("version", typeof<int>) |> ignore
              table.Rows[0]["version"] <- box 1
              expectInvariant "Wrong version type" (decode table)
          }

          test "empty identifier encoding is an invariant failure" {
              let table = makeTable ()
              table.Rows[0]["repository_ref"] <- box ""
              expectInvariant "Empty repository" (decode table)
          }

          test "unknown projection state is an invariant failure" {
              let table = makeTable ()
              table.Rows[0]["state"] <- box "UnknownState"
              expectInvariant "Unknown state" (decode table)
          }

          test "negative counts and partial check tuples are rejected" {
              let negative = makeTable ()
              negative.Rows[0]["checks_passed"] <- box -1
              negative.Rows[0]["checks_failed"] <- box 0
              negative.Rows[0]["checks_skipped"] <- box 0
              expectInvariant "Negative count" (decode negative)

              let partial = makeTable ()
              partial.Rows[0]["checks_passed"] <- box 1
              expectInvariant "Partial counts" (decode partial)
          }

          test "version below one is rejected" {
              let table = makeTable ()
              table.Rows[0]["version"] <- box 0L
              expectInvariant "Version zero" (decode table)
          }

          test "completed state requires both completion authorities" {
              let table = makeTable ()
              table.Rows[0]["state"] <- box "Completed"
              expectInvariant "Incomplete completed state" (decode table)
          }

          test "conflict state requires conflict evidence" {
              let table = makeTable ()
              table.Rows[0]["state"] <- box "Conflicted"
              expectInvariant "Conflict without evidence" (decode table)
          }

          test "non-conflict state cannot carry conflict-only data" {
              let table = makeTable ()
              table.Rows[0]["conflict_count"] <- box 1
              expectInvariant "Conflict data in started state" (decode table)
          }

          test "unknown outcome token is rejected rather than defaulted" {
              let table = makeTable ()
              table.Rows[0]["state"] <- box "FinishedWithoutStart"
              table.Rows[0]["started_journal_position"] <- DBNull.Value
              table.Rows[0]["repository_ref"] <- DBNull.Value
              table.Rows[0]["act_id"] <- DBNull.Value
              table.Rows[0]["leamas_version"] <- DBNull.Value
              table.Rows[0]["git_revision"] <- DBNull.Value
              table.Rows[0]["started_by"] <- DBNull.Value
              table.Rows[0]["started_at"] <- DBNull.Value
              table.Rows[0]["finished_journal_position"] <- box 1L
              table.Rows[0]["outcome"] <- box "unknown"
              table.Rows[0]["finished_at"] <- box timestamp
              table.Rows[0]["duration_ms"] <- box 1L
              table.Rows[0]["checks_passed"] <- box 1
              table.Rows[0]["checks_failed"] <- box 0
              table.Rows[0]["checks_skipped"] <- box 0
              expectInvariant "Unknown outcome" (decode table)
          }

          test "impossible authority positions are rejected" {
              let table = makeTable ()
              table.Rows[0]["started_journal_position"] <- box 3L
              table.Rows[0]["last_journal_position"] <- box 2L
              expectInvariant "Started after last" (decode table)
          } ]
