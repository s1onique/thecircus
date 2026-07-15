module Circus.Persistence.Postgres.Tests.MigrationTests

open System.Threading.Tasks
open Expecto
open Circus.Persistence.Postgres.Tests.PostgresFixture

let tests (fixture: PostgresFixture) =
    testList
        "Migration"
        [ test "schema applies to fresh database" {
              use conn = fixture.DataSource.CreateConnection()
              conn.OpenAsync() |> WaitTask |> ignore

              use cmd = conn.CreateCommand()
              cmd.CommandText <- "SELECT COUNT(*) FROM circus_event_journal"
              let count = cmd.ExecuteScalarAsync() |> WaitTask
              Expect.equal (int64 count) 0L "Journal should be empty"
          }

          test "expected constraints exist" {
              use conn = fixture.DataSource.CreateConnection()
              conn.OpenAsync() |> WaitTask |> ignore

              let checks =
                  [ "SELECT 1 FROM pg_constraint WHERE conname = 'circus_event_journal_source_event_id_uq'"
                    "SELECT 1 FROM pg_constraint WHERE conname = 'circus_event_journal_stream_sequence_uq'"
                    "SELECT 1 FROM pg_constraint WHERE conname = 'circus_event_journal_pkey'" ]

              for sql in checks do
                  use cmd = conn.CreateCommand()
                  cmd.CommandText <- sql
                  let result = cmd.ExecuteScalarAsync() |> WaitTask
                  Expect.isNotNull result "Constraint should exist"
          }

          test "expected indexes exist" {
              use conn = fixture.DataSource.CreateConnection()
              conn.OpenAsync() |> WaitTask |> ignore

              let checks =
                  [ "SELECT 1 FROM pg_indexes WHERE indexname = 'circus_event_journal_run_id_position_idx'"
                    "SELECT 1 FROM pg_indexes WHERE indexname = 'circus_event_journal_event_type_position_idx'"
                    "SELECT 1 FROM pg_indexes WHERE indexname = 'circus_event_journal_received_at_position_idx'" ]

              for sql in checks do
                  use cmd = conn.CreateCommand()
                  cmd.CommandText <- sql
                  let result = cmd.ExecuteScalarAsync() |> WaitTask
                  Expect.isNotNull result "Index should exist"
          }

          test "application role has restricted permissions" {
              use conn = fixture.DataSource.CreateConnection()
              conn.OpenAsync() |> WaitTask |> ignore

              // Check that circus_app role exists
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "SELECT 1 FROM pg_roles WHERE rolname = 'circus_app'"
              let result = cmd.ExecuteScalarAsync() |> WaitTask
              Expect.isNotNull result "Application role should exist"
          }

          test "update trigger prevents journal modification" {
              use conn = fixture.DataSource.CreateConnection()
              conn.OpenAsync() |> WaitTask |> ignore

              let sql = """
                  INSERT INTO circus_event_journal
                      (source, event_id, event_type, subject, observed_at,
                       instance_id, epoch_id, sequence, run_id, envelope_json, raw_body)
                  VALUES
                      ('test', 'id1', 'type1', 'subj', NOW(),
                       'inst', '00000000-0000-0000-0000-000000000001', 1, '00000000-0000-0000-0000-000000000001', '{}', E'\\x00')
              """

              use insertCmd = conn.CreateCommand()
              insertCmd.CommandText <- sql
              insertCmd.ExecuteNonQueryAsync() |> WaitTask |> ignore

              let updateSql = "UPDATE circus_event_journal SET subject = 'hacked' WHERE source = 'test'"
              use updateCmd = conn.CreateCommand()
              updateCmd.CommandText <- updateSql

              let ex = Expect.throws (fun () -> updateCmd.ExecuteNonQueryAsync() |> WaitTask |> ignore) "Update should be blocked"
              Expect.stringContains ex.Message "append-only" "Error should mention append-only"
          }

          test "delete trigger prevents journal deletion" {
              use conn = fixture.DataSource.CreateConnection()
              conn.OpenAsync() |> WaitTask |> ignore

              let sql = """
                  DELETE FROM circus_event_journal WHERE source = 'test'
              """
              use deleteCmd = conn.CreateCommand()
              deleteCmd.CommandText <- sql

              let ex = Expect.throws (fun () -> deleteCmd.ExecuteNonQueryAsync() |> WaitTask |> ignore) "Delete should be blocked"
              Expect.stringContains ex.Message "append-only" "Error should mention append-only"
          } ]
