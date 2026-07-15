module Circus.Persistence.Postgres.Tests.MigrationTests

open System
open Expecto
open Circus.Application
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.Support

let private execute (fixture: PostgresFixture) (request: IngestEventRequest) =
    fixture.Ingestion.Ingest request |> wait

let tests (fixture: PostgresFixture) =
    testList
        "Migration and least privilege"
        [ test "all application tables are in circus and no public fallback exists" {
              fixture.Reset()
              use conn = fixture.AdminDataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()

              cmd.CommandText <-
                  """
                  SELECT n.nspname, c.relname
                  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                  WHERE c.relname IN ('circus_event_journal','circus_run_projection','circus_schema_migrations')
                    AND c.relkind IN ('r','p')
                  ORDER BY c.relname
                  """

              use reader = cmd.ExecuteReader()
              let mutable rows = []

              while reader.Read() do
                  rows <- (reader.GetString(0), reader.GetString(1)) :: rows

              let result = List.rev rows

              Expect.equal
                  result
                  [ ("circus", "circus_event_journal")
                    ("circus", "circus_run_projection")
                    ("circus", "circus_schema_migrations") ]
                  "Every application table is circus-qualified"
          }

          test "constraints, indexes, trigger and sequence are circus-qualified" {
              use conn = fixture.AdminDataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()

              cmd.CommandText <-
                  """
                  SELECT
                    EXISTS (SELECT 1 FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'circus' AND c.conname = 'circus_event_journal_source_event_id_uq'),
                    EXISTS (SELECT 1 FROM pg_constraint c JOIN pg_namespace n ON n.oid = c.connamespace WHERE n.nspname = 'circus' AND c.conname = 'circus_event_journal_stream_sequence_uq'),
                    (to_regclass('circus.circus_event_journal_run_id_position_idx') IS NOT NULL),
                    (to_regclass('circus.circus_event_journal_journal_position_seq') IS NOT NULL),
                    (to_regprocedure('circus.prevent_journal_modification()') IS NOT NULL)
                  """

              use reader = cmd.ExecuteReader()
              Expect.isTrue (reader.Read()) "Catalog row exists"

              for index in 0..4 do
                  Expect.isTrue (reader.GetBoolean(index)) "Qualified object exists"
          }

          test "runtime role is not an owner, superuser or BYPASSRLS" {
              use conn = fixture.AdminDataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()

              cmd.CommandText <-
                  """
                  SELECT r.rolsuper, r.rolbypassrls,
                         pg_get_userbyid(j.relowner), pg_get_userbyid(p.relowner)
                  FROM pg_roles r
                  CROSS JOIN circus.circus_event_journal j
                  CROSS JOIN circus.circus_run_projection p
                  WHERE r.rolname = 'circus_app'
                  LIMIT 1
                  """

              use reader = cmd.ExecuteReader()
              Expect.isTrue (reader.Read()) "Runtime role exists"
              Expect.isFalse (reader.GetBoolean(0)) "Not superuser"
              Expect.isFalse (reader.GetBoolean(1)) "No BYPASSRLS"
              Expect.notEqual (reader.GetString(2)) "circus_app" "Journal owner is migration role"
              Expect.notEqual (reader.GetString(3)) "circus_app" "Projection owner is migration role"
          }

          test "positive ingestion succeeds through IngestEventService.Ingest" {
              fixture.Reset()

              let request =
                  requestWithFormatting (startedEvent "positive" (Guid.NewGuid()) (Guid.NewGuid()) 1L) false

              match execute fixture request with
              | Success(Inserted _, Some projection) ->
                  Expect.equal projection.Version 1L "First projection version is one"
              | other -> failwithf "Expected inserted result, got %A" other
          }

          test "restricted role UPDATE fails with a valid statement" {
              fixture.Reset()
              let runId = Guid.NewGuid()

              let request =
                  requestWithFormatting (startedEvent "negative-update" runId (Guid.NewGuid()) 1L) false

              execute fixture request |> ignore
              use conn = fixture.DataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()

              cmd.CommandText <-
                  "UPDATE circus.circus_event_journal SET subject = subject WHERE source = 'urn:test:producer'"

              Expect.throws (fun () -> cmd.ExecuteNonQuery() |> ignore) "Runtime UPDATE must fail"
          }

          test "restricted role DELETE fails with a valid statement" {
              fixture.Reset()

              let request =
                  requestWithFormatting (startedEvent "negative-delete" (Guid.NewGuid()) (Guid.NewGuid()) 1L) false

              execute fixture request |> ignore
              use conn = fixture.DataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "DELETE FROM circus.circus_event_journal WHERE source = 'urn:test:producer'"
              Expect.throws (fun () -> cmd.ExecuteNonQuery() |> ignore) "Runtime DELETE must fail"
          }

          test "restricted role TRUNCATE fails with a valid statement" {
              fixture.Reset()

              let request =
                  requestWithFormatting (startedEvent "negative-truncate" (Guid.NewGuid()) (Guid.NewGuid()) 1L) false

              execute fixture request |> ignore
              use conn = fixture.DataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()
              cmd.CommandText <- "TRUNCATE circus.circus_event_journal"
              Expect.throws (fun () -> cmd.ExecuteNonQuery() |> ignore) "Runtime TRUNCATE must fail"
          } ]
