module Circus.Persistence.Postgres.Tests.ProjectionInvariantTests

open System
open System.Data
open Expecto
open Circus.Application
open Circus.Domain
open Circus.Persistence.Postgres

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

let private mutate (table: DataTable) (name: string) (value: obj) = table.Rows[0][name] <- value

/// Apply the supplied mutations to a fresh started projection table.
let private withMutations (mutations: (string * obj) list) : DataTable =
    let table = makeTable ()

    for name, value in mutations do
        mutate table name value

    table

let private startedCompletedTable () =
    withMutations
        [ "state", box "Completed"
          "finished_journal_position", box 2L
          "outcome", box "succeeded"
          "finished_at", box timestamp
          "duration_ms", box 1000L
          "summary", box "summary"
          "checks_passed", box 1
          "checks_failed", box 0
          "checks_skipped", box 0
          "last_journal_position", box 2L
          "version", box 2L ]

let tests =
    testList
        "Projection decoder invariants"
        [ // Accepted edge cases
          test "started only is accepted" {
              let table = makeTable ()

              match decode table with
              | Ok projection ->
                  Expect.equal projection.Version 1L "Version"
                  Expect.equal projection.State StartedOnly "State"
              | Error _ -> failwith "Expected valid projection"
          }

          test "completed started-first is accepted" {
              let table = startedCompletedTable ()

              match decode table with
              | Ok projection ->
                  Expect.equal projection.State Completed "State"
                  Expect.equal projection.Version 2L "Version"
              | Error _ -> failwith "Expected valid completed projection"
          }

          test "finished-without-start is accepted" {
              let table =
                  withMutations
                      [ "state", box "FinishedWithoutStart"
                        "started_journal_position", DBNull.Value
                        "repository_ref", DBNull.Value
                        "act_id", DBNull.Value
                        "leamas_version", DBNull.Value
                        "git_revision", DBNull.Value
                        "started_by", DBNull.Value
                        "started_at", DBNull.Value
                        "finished_journal_position", box 1L
                        "outcome", box "succeeded"
                        "finished_at", box timestamp
                        "duration_ms", box 1000L
                        "summary", box "summary"
                        "checks_passed", box 1
                        "checks_failed", box 0
                        "checks_skipped", box 0
                        "version", box 1L ]

              match decode table with
              | Ok _ -> ()
              | Error _ -> failwith "Expected valid finished-without-start"
          }

          test "conflicted after duplicate started is accepted" {
              let table =
                  withMutations
                      [ "state", box "Conflicted"
                        "conflict_count", box 1
                        "last_journal_position", box 2L
                        "version", box 2L ]

              match decode table with
              | Ok _ -> ()
              | Error _ -> failwith "Expected valid conflicted projection"
          }


          // Position invariants
          test "started before first is rejected" {
              let table =
                  withMutations
                      [ "started_journal_position", box 2L
                        "last_journal_position", box 3L
                        "first_journal_position", box 5L
                        "version", box 1L ]

              expectInvariant "started before first" (decode table)
          }

          test "finished before first is rejected" {
              let table = startedCompletedTable ()
              mutate table "first_journal_position" (box 5L)
              expectInvariant "finished before first" (decode table)
          }

          test "authority after last is rejected" {
              let table =
                  withMutations
                      [ "started_journal_position", box 5L
                        "last_journal_position", box 4L
                        "version", box 1L ]

              expectInvariant "started after last" (decode table)
          }

          test "first greater than last is rejected" {
              let table =
                  withMutations [ "first_journal_position", box 5L; "last_journal_position", box 4L ]

              expectInvariant "first greater than last" (decode table)
          }

          test "started and finished at the same position are rejected" {
              let table =
                  withMutations
                      [ "state", box "Completed"
                        "started_journal_position", box 2L
                        "finished_journal_position", box 2L
                        "outcome", box "succeeded"
                        "finished_at", box timestamp
                        "duration_ms", box 1000L
                        "summary", box "summary"
                        "checks_passed", box 1
                        "checks_failed", box 0
                        "checks_skipped", box 0
                        "last_journal_position", box 2L
                        "version", box 2L ]

              expectInvariant "started equals finished" (decode table)
          }

          // Authority / state machine combinations
          test "started-only with finished event is rejected" {
              let table =
                  withMutations
                      [ "started_journal_position", box 1L
                        "finished_journal_position", box 2L
                        "last_journal_position", box 2L
                        "version", box 1L ]

              expectInvariant "started-only carries finished" (decode table)
          }

          test "finished-without-start with started event is rejected" {
              let table =
                  withMutations
                      [ "state", box "FinishedWithoutStart"
                        "started_journal_position", box 1L
                        "repository_ref", box "repository"
                        "leamas_version", box "1.0.0"
                        "started_at", box timestamp
                        "finished_journal_position", box 2L
                        "outcome", box "succeeded"
                        "finished_at", box timestamp
                        "duration_ms", box 1000L
                        "summary", box "summary"
                        "checks_passed", box 1
                        "checks_failed", box 0
                        "checks_skipped", box 0
                        "last_journal_position", box 2L
                        "version", box 1L ]

              expectInvariant "finished-without-start carries started" (decode table)
          }

          test "conflicted state without conflict_count is rejected" {
              let table = withMutations [ "state", box "Conflicted"; "version", box 1L ]

              expectInvariant "conflicted without conflict" (decode table)
          }

          test "non-conflicted state carrying conflict_count is rejected" {
              let table = makeTable ()
              mutate table "conflict_count" (box 1)
              expectInvariant "started with conflict_count" (decode table)
          }

          // Version / authority relation
          test "version below authorityCount + conflictCount is rejected" {
              let table = makeTable ()
              mutate table "version" (box 0L)
              expectInvariant "version zero" (decode table)
          }

          test "version above authorityCount + conflictCount is rejected" {
              let table =
                  withMutations
                      [ "state", box "Conflicted"
                        "conflict_count", box 1
                        "last_journal_position", box 2L
                        "version", box 5L ]

              expectInvariant "version inflated" (decode table)
          } ]
