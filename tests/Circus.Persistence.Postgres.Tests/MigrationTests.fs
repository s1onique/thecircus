module Circus.Persistence.Postgres.Tests.MigrationTests

open System
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open Expecto
open Npgsql
open Testcontainers.PostgreSql
open Circus.Application
open Circus.Persistence.Postgres
open Circus.Persistence.Postgres.Tests.PostgresFixture
open Circus.Persistence.Postgres.Tests.Support

let waitUnit (value: Task) = value.GetAwaiter().GetResult()
let wait (value: Task<'value>) = value.GetAwaiter().GetResult()

let private execute (dataSource: NpgsqlDataSource) (sql: string) =
    use conn = dataSource.OpenConnectionAsync().GetAwaiter().GetResult()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    cmd.ExecuteNonQuery() |> ignore

let private selectScalar (dataSource: NpgsqlDataSource) (sql: string) : obj =
    use conn = dataSource.OpenConnectionAsync().GetAwaiter().GetResult()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    cmd.ExecuteScalar()

let private tableNames (dataSource: NpgsqlDataSource) : (string * string) list =
    use conn = dataSource.OpenConnectionAsync().GetAwaiter().GetResult()
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

    List.rev rows

let private runMigrationAndAssertions
    (fixture: PostgresFixture)
    (label: string)
    (preMigrationSql: string option)
    : unit =
    let databaseName = $"circus_migration_{Guid.NewGuid():N}"

    let adminDataSource', runtimeDataSource' =
        fixture.CreateMigrationDatabase(databaseName)

    try
        match preMigrationSql with
        | Some sql -> execute adminDataSource' sql
        | None -> ()

        // Apply the production migration runner.
        Migration.migrate adminDataSource' |> waitUnit

        // Every application table lives in the circus namespace.
        let names = tableNames adminDataSource'

        Expect.equal
            names
            [ ("circus", "circus_event_journal")
              ("circus", "circus_run_projection")
              ("circus", "circus_schema_migrations") ]
            $"{label}: every application table is circus-qualified"

        // The migration ledger records the full three-version history.
        let versions =
            selectScalar
                adminDataSource'
                "SELECT array_agg(version ORDER BY version) FROM circus.circus_schema_migrations"
            |> string

        Expect.stringContains versions "000001_event_journal" $"{label}: 000001 recorded"
        Expect.stringContains versions "000002_namespace_alignment" $"{label}: 000002 recorded"
        Expect.stringContains versions "000003_runtime_grant_hardening" $"{label}: 000003 recorded"
    finally
        runtimeDataSource'.Dispose()
        adminDataSource'.Dispose()
        fixture.DropMigrationDatabase(databaseName)

let private releasedParentHasNoCircusOwner (dataSource: NpgsqlDataSource) =
    use conn = dataSource.OpenConnectionAsync().GetAwaiter().GetResult()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT COUNT(*) FROM pg_roles WHERE rolname = 'circus_owner'"
    let count = cmd.ExecuteScalar() |> string
    Expect.equal count "0" "Released-parent fixture has no circus_owner before the run"

let private circusOwnerExists (dataSource: NpgsqlDataSource) =
    use conn = dataSource.OpenConnectionAsync().GetAwaiter().GetResult()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'circus_owner')"
    let present = cmd.ExecuteScalar() |> string
    Expect.equal present "True" "circus_owner exists after the run"

/// A released-parent upgrade test must run inside an isolated PostgreSQL
/// cluster.  The shared `PostgresFixture` starts one container and runs
/// the current migrations against its main database before any migration
/// test executes; those migrations create `circus_owner` cluster-wide.
/// PostgreSQL roles are cluster-wide, not database-local, so a fresh
/// database created inside the shared cluster already has `circus_owner`
/// and the released-parent precondition can never be observed.  The
/// test therefore starts its own dedicated container, applies the
/// released-parent fixture before any current migration runs, asserts
/// the cluster-wide absence of `circus_owner`, and only then exercises
/// `Migration.migrate`.  The container is destroyed when the test
/// completes; the role does not leak into any other cluster.
let private runReleasedParentTest () =
    let databaseName = $"circus_released_parent_{Guid.NewGuid():N}"

    let container: PostgreSqlContainer =
        (new PostgreSqlBuilder("postgres:17.4"))
            .WithDatabase(databaseName)
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build()

    container.StartAsync() |> waitUnit

    let adminDataSource =
        NpgsqlDataSourceBuilder(container.GetConnectionString()).Build()

    try
        let releasedSql =
            let assembly = System.Reflection.Assembly.GetExecutingAssembly()

            let resourceName =
                assembly.GetManifestResourceNames()
                |> Array.tryFind (fun candidate ->
                    candidate.EndsWith("000001_released_parent.sql", StringComparison.Ordinal))

            match resourceName with
            | Some resource ->
                use stream = assembly.GetManifestResourceStream(resource)
                use reader = new System.IO.StreamReader(stream)
                reader.ReadToEnd()
            | None -> failwithf "fixture 000001_released_parent.sql not embedded in the test assembly"

        // Apply the released-parent fixture BEFORE any current migration
        // runs.  This is the exact released-upgrade state.
        execute adminDataSource releasedSql

        // Pre-condition: the released cluster has only circus_app;
        // circus_owner is intentionally absent.
        releasedParentHasNoCircusOwner adminDataSource

        // Apply the production migration runner.  Both 000001 and 000002
        // are recorded in the ledger so the runner applies only 000003.
        Migration.migrate adminDataSource |> waitUnit

        // Post-condition: circus_owner now exists cluster-wide.
        circusOwnerExists adminDataSource

        // The released-parent path records 000003 in the ledger.
        let versions =
            selectScalar
                adminDataSource
                "SELECT array_agg(version ORDER BY version) FROM circus.circus_schema_migrations"
            |> string

        Expect.stringContains versions "000001_event_journal" "released-parent: 000001 recorded"
        Expect.stringContains versions "000002_namespace_alignment" "released-parent: 000002 recorded"
        Expect.stringContains versions "000003_runtime_grant_hardening" "released-parent: 000003 recorded"

        // The catalog-driven digest-CHECK drop succeeded: the canonical
        // equality CHECK is the only digest CHECK on the table.  This
        // is the precise invariant that the released-parent path used
        // to break: a length-only CHECK named the same as the canonical
        // one was carried forward from the released 000002.
        let digestChecks =
            selectScalar
                adminDataSource
                """
                SELECT COUNT(*)
                  FROM pg_constraint c
                  JOIN pg_class t ON t.oid = c.conrelid
                  JOIN pg_namespace n ON n.oid = t.relnamespace
                 WHERE n.nspname = 'circus'
                   AND t.relname = 'circus_event_journal'
                   AND c.contype = 'c'
                   AND pg_get_constraintdef(c.oid) ILIKE '%raw_body_sha256%'
                """
            |> string

        Expect.equal digestChecks "1" "released-parent: exactly one digest-related CHECK remains after the upgrade"

        let constraintBody =
            selectScalar
                adminDataSource
                "SELECT pg_get_constraintdef(c.oid) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace WHERE n.nspname = 'circus' AND t.relname = 'circus_event_journal' AND c.conname = 'circus_event_journal_raw_body_sha256_ck'"
            |> string

        Expect.stringContains
            constraintBody
            "circus_extensions.digest"
            "released-parent: canonical CHECK uses schema-qualified digest()"

        Expect.stringContains
            constraintBody
            "raw_body_sha256"
            "released-parent: canonical CHECK references the digest column"

        // The extension schema is owned by circus_owner.
        let extOwner =
            selectScalar
                adminDataSource
                "SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'circus_extensions'"
            |> string

        Expect.equal extOwner "circus_owner" "released-parent: circus_extensions is owned by circus_owner"

        // pgcrypto is installed in the canonical extension schema.
        let extSchema =
            selectScalar
                adminDataSource
                "SELECT n.nspname FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'pgcrypto'"
            |> string

        Expect.equal extSchema "circus_extensions" "released-parent: pgcrypto is installed in circus_extensions"
    finally
        adminDataSource.Dispose()
        container.StopAsync() |> waitUnit

let tests (fixture: PostgresFixture) =
    testList
        "Migration and least privilege"
        [ test "fresh empty database migrates to canonical state" { runMigrationAndAssertions fixture "fresh" None }

          test "legacy already-applied 000001 public schema is reconciled by 000002 and 000003" {
              let legacySql = fixture.LoadFixture "000000_pre_closure.sql"
              runMigrationAndAssertions fixture "legacy" (Some legacySql)
          }

          test "released-parent 000001+000002 in circus ledger with circus_owner absent is corrected by 000003" {
              runReleasedParentTest ()
          }

          test "existing raw bytes survive the corrective migration byte-for-byte" {
              let legacySql = fixture.LoadFixture "000000_pre_closure.sql"
              let databaseName = $"circus_migration_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  execute adminDataSource' legacySql

                  let rawBytesBefore =
                      selectScalar
                          adminDataSource'
                          "SELECT raw_body FROM public.circus_event_journal WHERE event_id = 'legacy-event-1'"
                      :?> byte[]

                  Migration.migrate adminDataSource' |> waitUnit

                  let rawBytesAfter =
                      selectScalar
                          adminDataSource'
                          "SELECT raw_body FROM circus.circus_event_journal WHERE event_id = 'legacy-event-1'"
                      :?> byte[]

                  Expect.equal
                      (rawBytesAfter, rawBytesAfter.Length)
                      (rawBytesBefore, rawBytesBefore.Length)
                      "Raw body survived byte-for-byte"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "existing projection survives the corrective migration semantically" {
              let legacySql = fixture.LoadFixture "000000_pre_closure.sql"
              let databaseName = $"circus_migration_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  execute adminDataSource' legacySql
                  Migration.migrate adminDataSource' |> waitUnit

                  let state =
                      selectScalar
                          adminDataSource'
                          "SELECT state FROM circus.circus_run_projection WHERE run_id = '00000000-0000-0000-0000-0000000000a1'"
                      |> string

                  Expect.equal state "Completed" "Projection state survived"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "raw_body_sha256 is backfilled and non-null after the corrective migration" {
              let legacySql = fixture.LoadFixture "000000_pre_closure.sql"
              let databaseName = $"circus_migration_{Guid.NewGuid():N}"
              let adminDataSource', _ = fixture.CreateMigrationDatabase(databaseName)

              try
                  execute adminDataSource' legacySql
                  Migration.migrate adminDataSource' |> waitUnit

                  let digest =
                      selectScalar
                          adminDataSource'
                          "SELECT raw_body_sha256 FROM circus.circus_event_journal WHERE event_id = 'legacy-event-1'"
                      :?> byte[]

                  Expect.isNotNull digest "Digest is not null"
                  Expect.equal digest.Length 32 "Digest length is 32 bytes"

                  let rawBytes =
                      selectScalar
                          adminDataSource'
                          "SELECT raw_body FROM circus.circus_event_journal WHERE event_id = 'legacy-event-1'"
                      :?> byte[]

                  use sha = System.Security.Cryptography.SHA256.Create()
                  let computed = sha.ComputeHash rawBytes
                  Expect.equal digest computed "Backfilled digest matches raw bytes"
              finally
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "second migration run against the same database is a no-op" {
              let databaseName = $"circus_migration_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  Migration.migrate adminDataSource' |> waitUnit

                  let firstLedger =
                      selectScalar adminDataSource' "SELECT count(*) FROM circus.circus_schema_migrations"
                      |> string

                  // Second run should be a no-op: same row count, no exception.
                  Migration.migrate adminDataSource' |> waitUnit

                  let secondLedger =
                      selectScalar adminDataSource' "SELECT count(*) FROM circus.circus_schema_migrations"
                      |> string

                  Expect.equal firstLedger secondLedger "Migration ledger count is unchanged"
                  Expect.equal firstLedger "3" "All three versions recorded exactly once"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "migration runs to completion with Maximum Pool Size = 1" {
              // Encoded evidence that the runner does not open a second
              // pool slot while the advisory lock is held.  With
              // Maximum Pool Size = 1 the runner must use the locked
              // connection for ledger discovery and migration execution
              // without deadlocking.  The shared PostgresFixture
              // already migrates its main database on construction, so
              // we use a dedicated container with no prior migration
              // history and a pool of one to exercise this invariant.
              let databaseName = $"circus_poolsize1_{Guid.NewGuid():N}"

              let container: PostgreSqlContainer =
                  (new PostgreSqlBuilder("postgres:17.4"))
                      .WithDatabase(databaseName)
                      .WithUsername("postgres")
                      .WithPassword("postgres")
                      .Build()

              container.StartAsync() |> waitUnit

              try
                  let builder = NpgsqlConnectionStringBuilder(container.GetConnectionString())
                  builder.MaxPoolSize <- 1
                  builder.MinPoolSize <- 0

                  use adminDataSource = NpgsqlDataSourceBuilder(builder.ConnectionString).Build()

                  Migration.migrate adminDataSource |> waitUnit

                  let versions =
                      selectScalar
                          adminDataSource
                          "SELECT array_agg(version ORDER BY version) FROM circus.circus_schema_migrations"
                      |> string

                  Expect.stringContains
                      versions
                      "000003_runtime_grant_hardening"
                      "Pool-size-1 migration recorded 000003"
              finally
                  container.StopAsync() |> waitUnit
          }

          test "concurrent runner is rejected by the migration advisory lock" {
              // Encoded evidence that the migration advisory lock
              // rejects a second runner.  The lock must be held on a
              // dedicated, persistently open `NpgsqlConnection` so the
              // session advisory lock survives until the second
              // `Migration.migrate` call returns.  After releasing the
              // lock, the same runner succeeds without contention, so
              // the rejection is exactly the lock and not some other
              // bootstrap defect.
              let databaseName = $"circus_lock_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  // Hold the lock on a connection that is intentionally
                  // retained for the duration of the test.  Direct
                  // data-source execution does not provide an explicitly
                  // owned session across the lock, the assertion, and
                  // the unlock commands; the test therefore retains a
                  // dedicated open connection and releases the lock
                  // through that same session.  Returning a pooled
                  // connection to the pool on `use` does NOT end the
                  // PostgreSQL session and does NOT release the session
                  // advisory lock, so the lock survives until either
                  // explicit `pg_advisory_unlock` or physical session
                  // closure.
                  use lockConnection = adminDataSource'.OpenConnection()

                  use lockCommand = lockConnection.CreateCommand()
                  lockCommand.CommandText <- "SELECT pg_advisory_lock(@k)"
                  let p = lockCommand.CreateParameter()
                  p.ParameterName <- "k"
                  p.Value <- 0x43495243_55530001L
                  lockCommand.Parameters.Add(p) |> ignore
                  lockCommand.ExecuteScalar() |> ignore

                  let mutable caught: exn = null

                  try
                      try
                          Migration.migrate adminDataSource' |> waitUnit
                          ()
                      with ex ->
                          caught <- ex

                      Expect.isNotNull caught "Concurrent runner is rejected with an exception"

                      Expect.equal
                          (caught.GetType())
                          (typeof<MigrationInvariantException>)
                          "Concurrent runner is rejected with MigrationInvariantException"

                      Expect.stringContains
                          caught.Message
                          "another migration runner is already active for this database"
                          "Concurrent runner is rejected with the advisory-lock invariant message"
                  finally
                      // Release the lock on the same connection so the
                      // session lock is dropped before teardown.  We
                      // also close the connection afterwards to free
                      // the pool slot before DropMigrationDatabase.
                      use unlockCommand = lockConnection.CreateCommand()
                      unlockCommand.CommandText <- "SELECT pg_advisory_unlock(@k)"
                      let p = unlockCommand.CreateParameter()
                      p.ParameterName <- "k"
                      p.Value <- 0x43495243_55530001L
                      unlockCommand.Parameters.Add(p) |> ignore
                      unlockCommand.ExecuteScalar() |> ignore

                  // After release the same Migration.migrate call must
                  // succeed.  This proves the rejection above was caused
                  // by the held lock and not by some other bootstrap
                  // defect (for example, an inverted SET ROLE before
                  // every migration would have raised its own error
                  // here).
                  Migration.migrate adminDataSource' |> waitUnit

                  let versions =
                      selectScalar
                          adminDataSource'
                          "SELECT array_agg(version ORDER BY version) FROM circus.circus_schema_migrations"
                      |> string

                  Expect.stringContains
                      versions
                      "000003_runtime_grant_hardening"
                      "After lock release, Migration.migrate succeeds and records the full ledger"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "ambiguous dual-schema state fails with an explicit migration invariant" {
              let databaseName = $"circus_migration_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  execute
                      adminDataSource'
                      """
                      CREATE SCHEMA circus;
                      CREATE TABLE circus.circus_event_journal (id int);
                      CREATE TABLE public.circus_event_journal (id int);
                      """

                  Expect.throws
                      (fun () -> Migration.migrate adminDataSource' |> waitUnit)
                      "Ambiguous dual-schema state fails explicitly"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "failed migration is not recorded as applied" {
              let databaseName = $"circus_migration_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  // Pre-create the circus schema and ledger with the 000001
                  // version recorded. 000001 is then a no-op and 000002 runs
                  // but is engineered to fail inside this test by tampering
                  // with the digest backfill.
                  execute
                      adminDataSource'
                      """
                      CREATE SCHEMA IF NOT EXISTS circus;
                      CREATE TABLE circus.circus_schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT clock_timestamp());
                      INSERT INTO circus.circus_schema_migrations(version) VALUES ('000001_event_journal');
                      CREATE TABLE circus.circus_event_journal (journal_position bigint PRIMARY KEY);
                      -- Block the digest backfill by overriding digest().
                      CREATE OR REPLACE FUNCTION digest(bytea, text) RETURNS bytea LANGUAGE sql AS 'SELECT NULL::bytea';
                      """

                  try
                      Migration.migrate adminDataSource' |> waitUnit |> ignore
                  with _ ->
                      ()

                  let hasVersion2 =
                      selectScalar
                          adminDataSource'
                          "SELECT EXISTS (SELECT 1 FROM circus.circus_schema_migrations WHERE version = '000002_namespace_alignment')"
                      |> string

                  Expect.equal hasVersion2 "False" "Failed 000002 is not recorded as applied"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
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
                  Expect.isTrue (reader.GetBoolean index) "Qualified object exists"
          }

          // ---- D5 least-privilege catalog evidence ----
          test "runtime role is not a superuser, has no BYPASSRLS, and does not own anything" {
              use conn = fixture.AdminDataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()

              cmd.CommandText <-
                  """
                  SELECT
                    r.rolsuper,
                    r.rolbypassrls,
                    r.rolinherit,
                    -- journal owner
                    (SELECT pg_get_userbyid(c.relowner)
                       FROM pg_class c
                       JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'circus' AND c.relname = 'circus_event_journal'),
                    -- projection owner
                    (SELECT pg_get_userbyid(c.relowner)
                       FROM pg_class c
                       JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'circus' AND c.relname = 'circus_run_projection'),
                    -- ledger owner
                    (SELECT pg_get_userbyid(c.relowner)
                       FROM pg_class c
                       JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'circus' AND c.relname = 'circus_schema_migrations'),
                    -- sequence owner (pg_class.relowner, not seqowner)
                    (SELECT pg_get_userbyid(c.relowner)
                       FROM pg_class c
                       JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE n.nspname = 'circus' AND c.relname = 'circus_event_journal_journal_position_seq'),
                    -- trigger function owner
                    (SELECT pg_get_userbyid(p.proowner)
                       FROM pg_proc p
                       JOIN pg_namespace n ON p.pronamespace = n.oid
                      WHERE n.nspname = 'circus' AND p.proname = 'prevent_journal_modification'),
                    -- schema owner
                    (SELECT pg_get_userbyid(n.nspowner)
                       FROM pg_namespace n
                      WHERE n.nspname = 'circus')
                  FROM pg_roles r
                  WHERE r.rolname = 'circus_app'
                  """

              use reader = cmd.ExecuteReader()
              Expect.isTrue (reader.Read()) "Runtime role exists"
              Expect.isFalse (reader.GetBoolean 0) "Not superuser"
              Expect.isFalse (reader.GetBoolean 1) "No BYPASSRLS"
              Expect.isFalse (reader.GetBoolean 2) "NOINHERIT"
              Expect.notEqual (reader.GetString 3) "circus_app" "Journal owner is migration role"
              Expect.notEqual (reader.GetString 4) "circus_app" "Projection owner is migration role"
              Expect.notEqual (reader.GetString 5) "circus_app" "Ledger owner is migration role"
              Expect.notEqual (reader.GetString 6) "circus_app" "Sequence owner is migration role"
              Expect.notEqual (reader.GetString 7) "circus_app" "Trigger function owner is migration role"
              Expect.notEqual (reader.GetString 8) "circus_app" "Schema owner is migration role"
          }

          test "runtime role is not a member of the migration role and has no inherited destructive privileges" {
              use conn = fixture.AdminDataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()

              cmd.CommandText <-
                  """
                  SELECT
                    (SELECT NOT EXISTS (
                        SELECT 1 FROM pg_auth_members m
                          JOIN pg_roles a ON a.oid = m.roleid
                          JOIN pg_roles b ON b.oid = m.member
                         WHERE a.rolname = 'circus_owner' AND b.rolname = 'circus_app'
                     )),
                    (SELECT has_table_privilege('circus_app', 'circus.circus_event_journal', 'UPDATE')),
                    (SELECT has_table_privilege('circus_app', 'circus.circus_event_journal', 'DELETE')),
                    (SELECT has_table_privilege('circus_app', 'circus.circus_event_journal', 'TRUNCATE')),
                    (SELECT has_table_privilege('circus_app', 'circus.circus_event_journal', 'TRIGGER'))
                  """

              use reader = cmd.ExecuteReader()
              Expect.isTrue (reader.Read()) "Catalog row exists"
              Expect.isTrue (reader.GetBoolean 0) "Runtime role is not a member of the owner role"
              Expect.isFalse (reader.GetBoolean 1) "Runtime role has no UPDATE privilege on journal"
              Expect.isFalse (reader.GetBoolean 2) "Runtime role has no DELETE privilege on journal"
              Expect.isFalse (reader.GetBoolean 3) "Runtime role has no TRUNCATE privilege on journal"
              Expect.isFalse (reader.GetBoolean 4) "Runtime role has no TRIGGER privilege on journal"
          }

          test "positive ingestion succeeds through IngestEventService.Ingest" {
              fixture.Reset()

              let request =
                  compactRequest (startedEvent "positive" (Guid.NewGuid()) (Guid.NewGuid()) 1L)

              match fixture.Ingestion.Ingest request |> wait with
              | Success(Inserted _, Some projection) ->
                  Expect.equal projection.Version 1L "First projection version is one"
              | other -> failwithf "Expected inserted result, got %A" other
          }

          test "restricted role UPDATE fails with SQLSTATE 42501" {
              fixture.Reset()
              use conn = fixture.DataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()

              // The runtime role has only SELECT and INSERT on the journal.
              // The privilege check fails before any row is touched, so no
              // prior row is required.  We do not call IngestEventService
              // here: the test must not produce a fire-and-forget task that
              // outlives the test and contaminates the next test's state.
              cmd.CommandText <-
                  "UPDATE circus.circus_event_journal SET subject = subject WHERE source = 'urn:test:producer'"

              let mutable caught = false

              try
                  cmd.ExecuteNonQuery() |> ignore
              with
              | :? PostgresException as ex ->
                  caught <- true
                  Expect.equal ex.SqlState "42501" "Insufficient privilege SQLSTATE"
              | _ -> ()

              Expect.isTrue caught "Runtime UPDATE must fail"
          }

          test "restricted role DELETE fails with SQLSTATE 42501" {
              fixture.Reset()
              use conn = fixture.DataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()

              // The runtime role has only SELECT and INSERT on the journal.
              // No prior row is required; the privilege check fires before
              // any row is touched.
              cmd.CommandText <- "DELETE FROM circus.circus_event_journal WHERE source = 'urn:test:producer'"

              let mutable caught = false

              try
                  cmd.ExecuteNonQuery() |> ignore
              with
              | :? PostgresException as ex ->
                  caught <- true
                  Expect.equal ex.SqlState "42501" "Insufficient privilege SQLSTATE"
              | _ -> ()

              Expect.isTrue caught "Runtime DELETE must fail"
          }

          test "restricted role TRUNCATE fails with SQLSTATE 42501" {
              fixture.Reset()
              use conn = fixture.DataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()
              // The runtime role has no TRUNCATE privilege on the journal.
              // No prior row is required; the privilege check fires before
              // any row is touched.
              cmd.CommandText <- "TRUNCATE circus.circus_event_journal"

              let mutable caught = false

              try
                  cmd.ExecuteNonQuery() |> ignore
              with
              | :? PostgresException as ex ->
                  caught <- true
                  Expect.equal ex.SqlState "42501" "Insufficient privilege SQLSTATE"
              | _ -> ()

              Expect.isTrue caught "Runtime TRUNCATE must fail"
          }
          test "circus_app is not a member of circus_owner and cannot SET ROLE circus_owner" {
              use conn = fixture.AdminDataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()

              cmd.CommandText <-
                  """
                  SELECT
                    (SELECT pg_has_role('circus_app', 'circus_owner', 'MEMBER')),
                    (SELECT pg_has_role('circus_app', 'circus_owner', 'SET'))
                  """

              use reader = cmd.ExecuteReader()
              Expect.isTrue (reader.Read()) "Catalog row exists"
              Expect.isFalse (reader.GetBoolean 0) "circus_app is not a member of circus_owner (direct or indirect)"
              Expect.isFalse (reader.GetBoolean 1) "circus_app cannot SET ROLE circus_owner"
          }

          test "PUBLIC and circus_app do not hold CREATE on schema public" {
              use conn = fixture.AdminDataSource.CreateConnection()
              conn.Open()
              use cmd = conn.CreateCommand()

              cmd.CommandText <-
                  """
                  SELECT
                    (SELECT EXISTS (
                        SELECT 1
                          FROM pg_catalog.pg_namespace n
                          CROSS JOIN LATERAL pg_catalog.aclexplode(
                              coalesce(n.nspacl, pg_catalog.acldefault('n', n.nspowner))
                          ) AS acl
                         WHERE n.nspname = 'public'
                           AND acl.grantee = 0::oid
                           AND acl.privilege_type = 'CREATE'
                    )),
                    (SELECT has_schema_privilege('circus_app', 'public', 'CREATE'))
                  """

              use reader = cmd.ExecuteReader()
              Expect.isTrue (reader.Read()) "Catalog row exists"
              Expect.isFalse (reader.GetBoolean 0) "PUBLIC does not hold CREATE on schema public"
              Expect.isFalse (reader.GetBoolean 1) "circus_app does not hold CREATE on schema public"
          }

          test "migration ledger with a gap is rejected as non-canonical" {
              let databaseName = $"circus_migration_gap_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  // Plant an obviously broken ledger: 000001 followed
                  // directly by 000003 with 000002 missing.  The runner
                  // must reject this as a non-canonical prefix.
                  execute
                      adminDataSource'
                      """
                      CREATE SCHEMA circus;
                      CREATE TABLE circus.circus_schema_migrations (
                          version text PRIMARY KEY,
                          applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
                      );
                      CREATE TABLE circus.circus_event_journal (
                          journal_position bigint PRIMARY KEY,
                          raw_body bytea NOT NULL,
                          raw_body_sha256 bytea
                      );
                      INSERT INTO circus.circus_schema_migrations(version, applied_at)
                          VALUES ('000001_event_journal', '2026-01-01 00:00:01+00');
                      INSERT INTO circus.circus_schema_migrations(version, applied_at)
                          VALUES ('000003_runtime_grant_hardening', '2026-01-01 00:00:02+00');
                      """

                  Expect.throws
                      (fun () -> Migration.migrate adminDataSource' |> waitUnit)
                      "Ledger with a gap is rejected as non-canonical"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "migration ledger with an unknown version is rejected as non-canonical" {
              let databaseName = $"circus_migration_unknown_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  execute
                      adminDataSource'
                      """
                      CREATE SCHEMA circus;
                      CREATE TABLE circus.circus_schema_migrations (
                          version text PRIMARY KEY,
                          applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
                      );
                      CREATE TABLE circus.circus_event_journal (
                          journal_position bigint PRIMARY KEY,
                          raw_body bytea NOT NULL,
                          raw_body_sha256 bytea
                      );
                      INSERT INTO circus.circus_schema_migrations(version, applied_at)
                          VALUES ('000001_event_journal', '2026-01-01 00:00:01+00');
                      INSERT INTO circus.circus_schema_migrations(version, applied_at)
                          VALUES ('999999_future_migration', '2026-01-01 00:00:02+00');
                      """

                  Expect.throws
                      (fun () -> Migration.migrate adminDataSource' |> waitUnit)
                      "Ledger with an unknown version is rejected as non-canonical"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "migration ledger with a duplicate version is rejected as non-canonical" {
              // The previous round used `Set<string>` for the discovered
              // ledger and silently collapsed duplicates; the validator
              // could not distinguish a one-row ledger from a
              // duplicate-row ledger.  The new ordered-list discovery
              // lets the validator reject duplicate ledger rows
              // explicitly.  We plant a ledger table without a PRIMARY
              // KEY so PostgreSQL does not refuse the second INSERT;
              // the migration rejects the ledger before any migration
              // body runs.
              let databaseName = $"circus_migration_dup_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  execute
                      adminDataSource'
                      """
                      CREATE SCHEMA circus;
                      CREATE TABLE circus.circus_schema_migrations (
                          version text NOT NULL,
                          applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
                      );
                      CREATE TABLE circus.circus_event_journal (
                          journal_position bigint PRIMARY KEY,
                          raw_body bytea NOT NULL,
                          raw_body_sha256 bytea
                      );
                      INSERT INTO circus.circus_schema_migrations(version, applied_at)
                          VALUES ('000001_event_journal', '2026-01-01 00:00:01+00');
                      INSERT INTO circus.circus_schema_migrations(version, applied_at)
                          VALUES ('000001_event_journal', '2026-01-01 00:00:02+00');
                      INSERT INTO circus.circus_schema_migrations(version, applied_at)
                          VALUES ('000002_namespace_alignment', '2026-01-01 00:00:03+00');
                      """

                  let mutable caught: exn = null

                  try
                      Migration.migrate adminDataSource' |> waitUnit
                  with ex ->
                      caught <- ex

                  Expect.isNotNull caught "Ledger with a duplicate version is rejected with an exception"

                  Expect.equal
                      (caught.GetType())
                      (typeof<MigrationInvariantException>)
                      "Duplicate-version ledger is rejected with MigrationInvariantException"

                  Expect.stringContains
                      caught.Message
                      "non-canonical migration ledger: duplicate version '000001_event_journal'"
                      "Duplicate-version ledger message names the repeated canonical version"

                  // Only the pre-recorded duplicate is the cause; the
                  // migration body never executed, so the ledger still
                  // contains exactly the three rows the test planted
                  // (no migration version has been added).
                  let count =
                      selectScalar adminDataSource' "SELECT COUNT(*) FROM circus.circus_schema_migrations" :?> int64

                  Expect.equal count 3L "Ledger row count is unchanged when the duplicate is rejected"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "released-parent prefix 000001+000002 is accepted and only 000003 is applied without refresh" {
              // Encoded evidence that the ordered-list validator
              // accepts a contiguous historical prefix and that the
              // runner applies only the entries following the prefix.
              // The previous round's test invented a partial schema
              // that no released migration produced; the actual
              // production fixture already has the full `circus.*`
              // schema (released 000001 + released 000002) and a
              // ledger recorded by the released-parent fixture, so
              // this test reuses `000001_released_parent.sql` to make
              // the schema production-equivalent.  The runner must
              // execute only 000003 in the migration loop and must
              // NOT re-insert the seeded 000001 / 000002 rows.
              let databaseName = $"circus_migration_prefix_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  // Released 000001 + 000002 in the canonical ledger
                  // with the full canonical schema (see
                  // tests/fixtures/migrations/000001_released_parent.sql).
                  execute adminDataSource' (fixture.LoadFixture "000001_released_parent.sql")

                  // Capture the seeded applied_at of 000001 and
                  // 000002 before the runner runs so we can prove the
                  // rows survive byte-for-byte.
                  let seeded001 =
                      selectScalar
                          adminDataSource'
                          "SELECT applied_at FROM circus.circus_schema_migrations WHERE version = '000001_event_journal'"
                      |> string

                  let seeded002 =
                      selectScalar
                          adminDataSource'
                          "SELECT applied_at FROM circus.circus_schema_migrations WHERE version = '000002_namespace_alignment'"
                      |> string

                  Migration.migrate adminDataSource' |> waitUnit

                  let versions =
                      selectScalar
                          adminDataSource'
                          "SELECT array_agg(version ORDER BY version) FROM circus.circus_schema_migrations"
                      |> string

                  Expect.stringContains versions "000001_event_journal" "Prefix test: 000001 is preserved"
                  Expect.stringContains versions "000002_namespace_alignment" "Prefix test: 000002 is preserved"
                  Expect.stringContains versions "000003_runtime_grant_hardening" "Prefix test: 000003 is applied"

                  let count =
                      selectScalar adminDataSource' "SELECT COUNT(*) FROM circus.circus_schema_migrations" :?> int64

                  Expect.equal count 3L "Prefix test: exactly three ledger rows exist"

                  // The seeded 000001 / 000002 rows must remain
                  // byte-for-byte intact.
                  let later001 =
                      selectScalar
                          adminDataSource'
                          "SELECT applied_at FROM circus.circus_schema_migrations WHERE version = '000001_event_journal'"
                      |> string

                  let later002 =
                      selectScalar
                          adminDataSource'
                          "SELECT applied_at FROM circus.circus_schema_migrations WHERE version = '000002_namespace_alignment'"
                      |> string

                  Expect.equal later001 seeded001 "Prefix test: 000001 applied_at is unchanged"
                  Expect.equal later002 seeded002 "Prefix test: 000002 applied_at is unchanged"

                  // Re-run must be a true no-op.
                  Migration.migrate adminDataSource' |> waitUnit

                  let count2 =
                      selectScalar adminDataSource' "SELECT COUNT(*) FROM circus.circus_schema_migrations" :?> int64

                  Expect.equal count2 3L "Prefix test: re-running the migration does not change the ledger row count"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "out-of-order historical applied_at is rejected as non-canonical" {
              // Encoded evidence that the ordering authority is
              // `applied_at` (insertion time), not the canonical
              // migration name.  Two rows planted in the wrong order
              // with explicit monotonic `applied_at` values expose
              // the validator to a ledger whose canonical-name sort
              // would silently accept the result.  The runner must
              // reject the ledger with the canonical-prefix message
              // naming the ordered-but-out-of-canonical list.
              let databaseName = $"circus_migration_out_of_order_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  execute
                      adminDataSource'
                      """
                      CREATE SCHEMA circus;
                      -- A PKless ledger so the runner's precondition
                      -- on `pg_tables` does not lock us into one
                      -- version per row; we keep the production
                      -- applied_at column.
                      CREATE TABLE circus.circus_schema_migrations (
                          version text NOT NULL,
                          applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
                      );
                      CREATE TABLE circus.circus_event_journal (
                          journal_position bigint PRIMARY KEY,
                          raw_body bytea NOT NULL,
                          raw_body_sha256 bytea
                      );
                      -- Insert 000002 FIRST in real time, then 000001.
                      INSERT INTO circus.circus_schema_migrations(version, applied_at)
                          VALUES ('000002_namespace_alignment', '2026-01-01 12:00:01+00');
                      INSERT INTO circus.circus_schema_migrations(version, applied_at)
                          VALUES ('000001_event_journal', '2026-01-01 12:00:02+00');
                      """

                  let mutable caught: exn = null

                  try
                      Migration.migrate adminDataSource' |> waitUnit
                  with ex ->
                      caught <- ex

                  Expect.isNotNull caught "Out-of-order applied_at ledger is rejected with an exception"

                  Expect.equal
                      (caught.GetType())
                      (typeof<MigrationInvariantException>)
                      "Out-of-order applied_at ledger is rejected with MigrationInvariantException"

                  Expect.stringContains
                      caught.Message
                      "non-canonical migration ledger: versions [000002_namespace_alignment; 000001_event_journal] are not a contiguous prefix of [000001_event_journal; 000002_namespace_alignment; 000003_runtime_grant_hardening]"
                      "Out-of-order applied_at ledger message names the historical order"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "equal applied_at values fail closed instead of inventing historical order" {
              // PostgreSQL timestamps have finite microsecond resolution.
              // Two ledger rows can therefore share an applied_at value;
              // version order is deterministic but is not historical
              // evidence.  The runner must reject this ambiguous ledger
              // rather than canonicalizing it by version.
              let databaseName = $"circus_migration_equal_applied_at_{Guid.NewGuid():N}"

              let adminDataSource', runtimeDataSource' =
                  fixture.CreateMigrationDatabase(databaseName)

              try
                  execute
                      adminDataSource'
                      """
                      CREATE SCHEMA circus;
                      CREATE TABLE circus.circus_schema_migrations (
                          version text NOT NULL,
                          applied_at timestamptz NOT NULL
                      );
                      CREATE TABLE circus.circus_event_journal (
                          journal_position bigint PRIMARY KEY,
                          raw_body bytea NOT NULL,
                          raw_body_sha256 bytea
                      );
                      -- The historical write order was 000002, then 000001,
                      -- but both rows have the same recorded timestamp.
                      INSERT INTO circus.circus_schema_migrations(version, applied_at)
                          VALUES ('000002_namespace_alignment', '2026-01-01 12:00:00.000001+00');
                      INSERT INTO circus.circus_schema_migrations(version, applied_at)
                          VALUES ('000001_event_journal', '2026-01-01 12:00:00.000001+00');
                      """

                  let mutable caught: exn = null

                  try
                      Migration.migrate adminDataSource' |> waitUnit
                  with ex ->
                      caught <- ex

                  Expect.isNotNull caught "Equal-applied_at ledger is rejected with an exception"

                  Expect.equal
                      (caught.GetType())
                      (typeof<MigrationInvariantException>)
                      "Equal-applied_at ledger is rejected with MigrationInvariantException"

                  Expect.stringContains
                      caught.Message
                      "multiple versions share the same applied_at"
                      "Equal-applied_at message explains that historical order cannot be reconstructed"
              finally
                  runtimeDataSource'.Dispose()
                  adminDataSource'.Dispose()
                  fixture.DropMigrationDatabase(databaseName)
          }

          test "default privileges from circus_owner normalize PUBLIC grants across all six scopes" {
              // Default privileges are global+schema union: a single
              // ALTER DEFAULT PRIVILEGES for the role is not enough,
              // because PostgreSQL combines role-wide and
              // schema-scoped default ACLs.  The migration declares
              // revokes for tables, sequences, and functions in both
              // scope forms so that the union leaves PUBLIC with no
              // privileges regardless of where the creator role
              // creates the next object.  This test exercises all six
              // effective scopes the migration normalizes (three
              // role-wide and three schema-local for tables, sequences,
              // and functions) and asserts that:
              //   1) the planted catalog grants exist before the
              //      migration, including the exact schema-scoped
              //      function row;
              //   2) the global function default is effective before
              //      migration by probing a function in another schema;
              //   3) the migration's step 12b catalog assertion
              //      fires cleanly (no residual PUBLIC grant);
              //   4) a freshly-created table, sequence, and function
              //      under `circus_owner` carries no PUBLIC privileges;
              //   5) a second migration no-op is followed by a second
              //      future-object probe, not merely a recheck of old ACLs.
              //
              // The previous round's variant planted a
              // `REVOKE USAGE ON SEQUENCES FROM PUBLIC` and called it
              // a "grant" and asserted a fixed `pg_default_acl` row
              // count; both were wrong (the revoke is not a positive
              // grant and PostgreSQL deletes catalog entries whose
              // ACL equals the default).  The corrected variant
              // verifies the five catalog grants plus the global
              // hard-wired function default through a pre-migration
              // probe, then checks real future objects created under
              // `circus_owner` both before and after the no-op rerun.
              //
              // `circus_owner` is cluster-wide, so this test runs in
              // its own dedicated container so the cross-test
              // cluster state does not contaminate the assertion.
              let databaseName = $"circus_default_privs_{Guid.NewGuid():N}"

              let container: PostgreSqlContainer =
                  (new PostgreSqlBuilder("postgres:17.4"))
                      .WithDatabase(databaseName)
                      .WithUsername("postgres")
                      .WithPassword("postgres")
                      .Build()

              container.StartAsync() |> waitUnit

              let adminDataSource =
                  NpgsqlDataSourceBuilder(container.GetConnectionString()).Build()

              // Does circus_owner retain any non-empty PUBLIC default
              // grant (in any scope) for the canonical object kinds?
              let privatePublicGrantsFromCircusOwner (ds: NpgsqlDataSource) =
                  selectScalar
                      ds
                      """
                      SELECT EXISTS (
                          SELECT 1
                            FROM pg_catalog.pg_default_acl d
                            JOIN pg_catalog.pg_roles r
                              ON r.oid = d.defaclrole
                            JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) AS acl
                              ON true
                           WHERE r.rolname = 'circus_owner'
                             AND d.defaclobjtype IN ('r', 'S', 'f')
                             AND acl.grantee = 0::oid
                             AND acl.privilege_type IS NOT NULL
                             AND acl.privilege_type <> ''
                      )
                      """
                  :?> bool

              // Specific PUBLIC object-kind check, used for the table
              // and sequence catalog preconditions.
              let publicGrantForKind (ds: NpgsqlDataSource) (objtype: char) =
                  selectScalar
                      ds
                      $"""
                      SELECT EXISTS (
                          SELECT 1
                            FROM pg_catalog.pg_default_acl d
                            JOIN pg_catalog.pg_roles r
                              ON r.oid = d.defaclrole
                            JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) AS acl
                              ON true
                           WHERE r.rolname = 'circus_owner'
                             AND d.defaclobjtype = '{objtype}'
                             AND acl.grantee = 0::oid
                             AND acl.privilege_type IS NOT NULL
                             AND acl.privilege_type <> ''
                      )
                      """
                  :?> bool

              // Verify the exact row that the missing migration statement
              // must repair: circus_owner, circus namespace, functions,
              // PUBLIC grantee, EXECUTE privilege.
              let schemaScopedPublicFunctionGrant (ds: NpgsqlDataSource) =
                  selectScalar
                      ds
                      """
                      SELECT EXISTS (
                          SELECT 1
                            FROM pg_catalog.pg_default_acl d
                            JOIN pg_catalog.pg_roles r
                              ON r.oid = d.defaclrole
                            JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) AS acl
                              ON true
                           WHERE r.rolname = 'circus_owner'
                             AND d.defaclnamespace = 'circus'::regnamespace
                             AND d.defaclobjtype = 'f'
                             AND acl.grantee = 0::oid
                             AND acl.privilege_type = 'EXECUTE'
                      )
                      """
                  :?> bool

              let hasAnyPublicTablePrivilege (ds: NpgsqlDataSource) (tableName: string) =
                  selectScalar
                      ds
                      $"""
                      SELECT EXISTS (
                          SELECT 1
                            FROM pg_catalog.pg_class c
                            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                            CROSS JOIN LATERAL pg_catalog.aclexplode(
                                coalesce(c.relacl, pg_catalog.acldefault('r', c.relowner))
                            ) AS acl
                           WHERE n.nspname || '.' || c.relname = '{tableName}'
                             AND acl.grantee = 0::oid
                      )
                      """
                  :?> bool

              // The sequence-default object code is lowercase 's' for
              // `acldefault()`, not the uppercase 'S' that
              // `pg_default_acl.defaclobjtype` uses.  Mixing the two
              // catalog conventions silently evaluates the wrong
              // canonical hard-wired ACL.  See
              // https://www.postgresql.org/docs/current/functions-info.html
              // and the regression assertion below.
              let hasAnyPublicSequencePrivilege (ds: NpgsqlDataSource) (sequenceName: string) =
                  selectScalar
                      ds
                      $"""
                      SELECT EXISTS (
                          SELECT 1
                            FROM pg_catalog.pg_class c
                            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                            CROSS JOIN LATERAL pg_catalog.aclexplode(
                                coalesce(c.relacl, pg_catalog.acldefault('s', c.relowner))
                            ) AS acl
                           WHERE n.nspname || '.' || c.relname = '{sequenceName}'
                             AND c.relkind = 'S'
                             AND acl.grantee = 0::oid
                      )
                      """
                  :?> bool

              let hasPublicFunctionExecute (ds: NpgsqlDataSource) (functionName: string) =
                  selectScalar
                      ds
                      $"""
                      SELECT EXISTS (
                          SELECT 1
                            FROM pg_catalog.pg_proc p
                            CROSS JOIN LATERAL pg_catalog.aclexplode(
                                coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))
                            ) AS acl
                           WHERE p.oid = pg_catalog.to_regprocedure('{functionName}')
                             AND acl.grantee = 0::oid
                             AND acl.privilege_type = 'EXECUTE'
                      )
                      """
                  :?> bool

              // Regression assertions live immediately after
              // `createPrivilegeProbe adminDataSource "first"` below;
              // they cannot run before the probe exists because the
              // schema-qualified `relacl IS NULL` lookup would return
              // no row and the `:?> bool` cast would throw on a null
              // `ExecuteScalar()`.

              let createPrivilegeProbe (ds: NpgsqlDataSource) (suffix: string) =
                  execute
                      ds
                      $"""
                      SET ROLE circus_owner;
                      CREATE TABLE circus.circus_probe_table_{suffix} (id int);
                      CREATE SEQUENCE circus.circus_probe_seq_{suffix};
                      CREATE FUNCTION circus.circus_probe_fn_{suffix}() RETURNS int LANGUAGE sql AS 'SELECT 1';
                      RESET ROLE;
                      """

              let assertProbeHasNoPublicPrivileges (ds: NpgsqlDataSource) (suffix: string) (label: string) =
                  Expect.isFalse
                      (hasAnyPublicTablePrivilege ds $"circus.circus_probe_table_{suffix}")
                      $"{label}: PUBLIC has no privilege on the future table"

                  Expect.isFalse
                      (hasAnyPublicSequencePrivilege ds $"circus.circus_probe_seq_{suffix}")
                      $"{label}: PUBLIC has no privilege on the future sequence"

                  Expect.isFalse
                      (hasPublicFunctionExecute ds $"circus.circus_probe_fn_{suffix}()")
                      $"{label}: PUBLIC has no EXECUTE on the future function"

              try
                  // Use the full released-parent state so this test runs
                  // only 000003.  Running 000001 first would already
                  // revoke the planted schema-scoped function grant.
                  execute adminDataSource (fixture.LoadFixture "000001_released_parent.sql")

                  execute
                      adminDataSource
                      """
                      CREATE ROLE circus_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                      GRANT CREATE ON SCHEMA circus TO circus_owner;
                      CREATE SCHEMA circus_default_probe AUTHORIZATION circus_owner;
                      -- 1. Role-wide PUBLIC grant on TABLES.
                      ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner GRANT INSERT, UPDATE ON TABLES TO PUBLIC;
                      -- 2. Role-wide PUBLIC grant on SEQUENCES.
                      ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner GRANT USAGE, SELECT ON SEQUENCES TO PUBLIC;
                      -- 3. A global function PUBLIC EXECUTE default is
                      --    hard-wired by PostgreSQL, so it has no positive
                      --    pg_default_acl row to plant.  The test probes it
                      --    separately below before the migration.
                      -- 4. Schema-scoped PUBLIC grant on TABLES.
                      ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner IN SCHEMA circus GRANT INSERT ON TABLES TO PUBLIC;
                      -- 5. Schema-scoped PUBLIC grant on SEQUENCES.
                      ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner IN SCHEMA circus GRANT USAGE ON SEQUENCES TO PUBLIC;
                      -- 6. Schema-scoped PUBLIC grant on FUNCTIONS.
                      ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner IN SCHEMA circus GRANT EXECUTE ON FUNCTIONS TO PUBLIC;
                      SET ROLE circus_owner;
                      CREATE FUNCTION circus.circus_pre_migration_schema_fn() RETURNS int LANGUAGE sql AS 'SELECT 1';
                      CREATE FUNCTION circus_default_probe.circus_pre_migration_global_fn() RETURNS int LANGUAGE sql AS 'SELECT 1';
                      RESET ROLE;
                      """

                  // Pre-condition: role-wide table and sequence grants,
                  // and the exact schema-scoped function grant, exist in
                  // pg_default_acl before 000003 runs.
                  Expect.isTrue
                      (publicGrantForKind adminDataSource 'r')
                      "Pre-condition: PUBLIC holds a default TABLES grant from circus_owner before the migration"

                  Expect.isTrue
                      (publicGrantForKind adminDataSource 'S')
                      "Pre-condition: PUBLIC holds a default SEQUENCES grant from circus_owner before the migration"

                  Expect.isTrue
                      (schemaScopedPublicFunctionGrant adminDataSource)
                      "Pre-condition: exact circus-scoped PUBLIC EXECUTE function default row exists"

                  // The global function default is not represented by a
                  // positive pg_default_acl row.  A function created by
                  // circus_owner outside `circus` proves that the
                  // hard-wired global PUBLIC EXECUTE default is effective.
                  Expect.isTrue
                      (hasPublicFunctionExecute adminDataSource "circus_default_probe.circus_pre_migration_global_fn()")
                      "Pre-condition: PUBLIC can EXECUTE a global-default probe function"

                  // Apply the production migration.
                  Migration.migrate adminDataSource |> waitUnit

                  // Post-condition (catalog): no non-empty PUBLIC
                  // default grant from circus_owner for any of the
                  // canonical object kinds.  This crosses the
                  // migration's step 12b assertion.
                  Expect.isFalse
                      (privatePublicGrantsFromCircusOwner adminDataSource)
                      "Post-condition: PUBLIC holds no non-empty default grants from circus_owner after the migration"

                  // Probe objects created after 000003, then run the
                  // migration again.  The second set is created after the
                  // no-op rerun because default privileges are future-only;
                  // checking the first set again would not prove rerun
                  // stability for future objects.
                  createPrivilegeProbe adminDataSource "first"

                  // Regression assertion (must run AFTER the probe is
                  // created).  A freshly-created sequence has
                  // `relacl IS NULL` and therefore relies on the
                  // `acldefault()` fallback.  The corrected
                  // lowercase-`s` fallback must report that PUBLIC has
                  // no entry in the hard-wired default.  An uppercase-`S`
                  // fallback is the FOREIGN-SERVER code and would
                  // silently mis-evaluate.  This test freezes the exact
                  // branch that the prior round carried wrong.  The
                  // schema-qualified lookup joins `pg_namespace` so
                  // the assertion cannot accidentally match a
                  // same-named sequence in another schema, and the
                  // `relkind = 'S'` filter restricts the lookup to
                  // sequences even if a future migration introduces a
                  // relation with the same name.  Failing explicitly
                  // when the row is absent prevents the prior round's
                  // null-scalar cast crash from hiding the real
                  // regression.
                  let relAclIsNull =
                      selectScalar
                          adminDataSource
                          $"""
                          SELECT c.relacl IS NULL
                            FROM pg_catalog.pg_class c
                            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                           WHERE n.nspname = 'circus'
                             AND c.relname = 'circus_probe_seq_first'
                             AND c.relkind = 'S'
                          """

                  Expect.isNotNull relAclIsNull "Probe sequence exists"
                  Expect.isTrue (unbox<bool> relAclIsNull) "Probe sequence uses the acldefault fallback"

                  Expect.isFalse
                      (selectScalar
                          adminDataSource
                          $"""
                          SELECT EXISTS (
                              SELECT 1
                                FROM pg_catalog.pg_class c
                                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                                CROSS JOIN LATERAL pg_catalog.aclexplode(
                                    pg_catalog.acldefault('s', c.relowner)
                                ) AS acl
                               WHERE n.nspname = 'circus'
                                 AND c.relname = 'circus_probe_seq_first'
                                 AND c.relkind = 'S'
                                 AND acl.grantee = 0::oid
                          )
                          """
                      :?> bool)
                      "Regression: lowercase 's' acldefault reports no PUBLIC entry on the probe sequence"

                  assertProbeHasNoPublicPrivileges adminDataSource "first" "First post-migration probe"

                  Migration.migrate adminDataSource |> waitUnit

                  Expect.isFalse
                      (privatePublicGrantsFromCircusOwner adminDataSource)
                      "Re-run: PUBLIC still holds no non-empty default grants from circus_owner"

                  createPrivilegeProbe adminDataSource "second"
                  assertProbeHasNoPublicPrivileges adminDataSource "second" "Second post-rerun future-object probe"
              finally
                  adminDataSource.Dispose()
                  container.StopAsync() |> waitUnit
          } ]
