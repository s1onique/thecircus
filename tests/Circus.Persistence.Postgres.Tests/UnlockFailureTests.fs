module Circus.Persistence.Postgres.Tests.UnlockFailureTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open Testcontainers.PostgreSql
open Circus.Persistence.Postgres
open Circus.Persistence.Postgres.Tests.PostgresFixture

let waitUnit (value: Task) = value.GetAwaiter().GetResult()
let wait (value: Task<'value>) = value.GetAwaiter().GetResult()

/// Real PostgreSQL primitives that the production migration runner uses.
/// The unlock-failure tests delegate to these on the acquire path so the
/// session advisory lock is genuinely held, then inject a deterministic
/// release failure on the release / clear-pool path.
let private realTryAcquire: NpgsqlConnection -> Task<bool> =
    Migration.MigrationLockOperations.real.TryAcquire

let private realClearPool: NpgsqlConnection -> unit =
    Migration.MigrationLockOperations.real.ClearPool

/// Lock operations that delegate to the real PostgreSQL primitives for
/// acquire, force the release path to report `false`, and exercise the
/// real `NpgsqlConnection.ClearPool` (not a no-op).  Returns the
/// operations record together with a getter that exposes the observed
/// call count so the caller can assert the runner really invoked the
/// pool-clear seam.
///
/// The previous review flagged that the test seam previously substituted
/// a no-op `ClearPool` for the real one.  Because Npgsql's pooled
/// connection reset (`DISCARD ALL`) invokes `pg_advisory_unlock_all()`
/// on the next acquisition, a no-op `ClearPool` could not be
/// distinguished from the real one by observing the next
/// `pg_try_advisory_lock` alone.  This seam counts and forwards to the
/// real `ClearPool` so the test fails if the runner bypasses it.
let private releaseFailsWithRealClearPool () : Migration.MigrationLockOperations.Operations * (unit -> int) =
    let mutable clearCalls = 0

    let ops: Migration.MigrationLockOperations.Operations =
        { TryAcquire = realTryAcquire
          Release = fun (_: NpgsqlConnection) -> Task.FromResult false
          ClearPool =
            fun (conn: NpgsqlConnection) ->
                clearCalls <- clearCalls + 1
                realClearPool conn }

    ops, (fun () -> clearCalls)

/// Start a fresh dedicated PostgreSQL container so the test owns every
/// role and every object on the cluster.  PostgreSQL roles are
/// cluster-wide, so the unlock-failure tests cannot share the shared
/// `PostgresFixture` container and still observe a clean recovery.
let private startIsolatedContainer (databaseName: string) =
    let container: PostgreSqlContainer =
        (new PostgreSqlBuilder("postgres:17.4"))
            .WithDatabase(databaseName)
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build()

    container.StartAsync() |> waitUnit
    container

let private adminDataSourceFor (container: PostgreSqlContainer) =
    NpgsqlDataSourceBuilder(container.GetConnectionString()).Build()

/// Apply the released-parent fixture: 000001 and 000002 are recorded in
/// the canonical `circus.circus_schema_migrations` ledger, the
/// `circus.*` schema and tables exist, `circus_app` exists, and
/// `circus_owner` does NOT exist.  This is the exact released-upgrade
/// state the next migration version has to repair.
let private applyReleasedParent (adminDataSource: NpgsqlDataSource) =
    let assembly = System.Reflection.Assembly.GetExecutingAssembly()

    let resourceName =
        assembly.GetManifestResourceNames()
        |> Array.tryFind (fun candidate -> candidate.EndsWith("000001_released_parent.sql", StringComparison.Ordinal))

    let sql =
        match resourceName with
        | Some resource ->
            use stream = assembly.GetManifestResourceStream(resource)
            use reader = new System.IO.StreamReader(stream)
            reader.ReadToEnd()
        | None -> failwithf "fixture 000001_released_parent.sql not embedded in the test assembly"

    use conn = adminDataSource.OpenConnection()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    cmd.ExecuteNonQuery() |> ignore

/// Bounded poll for the disappearance of a backend PID.  Npgsql
/// guarantees that the busy physical connection returned to the pool
/// after `ClearPool` is closed when it is finally returned, but it does
/// not guarantee that another PostgreSQL session observes the row
/// disappearance in `pg_stat_activity` synchronously.  A polling loop
/// with a 5-second budget keeps the assertion robust against
/// `pg_stat_activity` lag without making the test wait forever on a
/// real defect.
let private waitForPidDisappearance (adminDataSource: NpgsqlDataSource) (pid: int) : bool =
    use probeConn = adminDataSource.OpenConnection()

    let deadline = DateTime.UtcNow.AddSeconds 5.0
    let mutable stillActive = true

    while stillActive && DateTime.UtcNow < deadline do
        use probeCmd = probeConn.CreateCommand()
        probeCmd.CommandText <- "SELECT COUNT(*) FROM pg_stat_activity WHERE pid = @pid"
        let pidParam = probeCmd.CreateParameter()
        pidParam.ParameterName <- "pid"
        pidParam.Value <- pid
        probeCmd.Parameters.Add(pidParam) |> ignore

        let count = probeCmd.ExecuteScalar() |> unbox<int64>

        stillActive <- count > 0L

        if stillActive then
            Thread.Sleep 50

    stillActive

/// Assert the cleanup exception's `Message` and `InnerException`.
/// Production declares `MigrationLockCleanupException` as a sealed
/// class deriving from `Exception(message, inner)` so the
/// `InnerException` property is properly wired (F# `exception ... of
/// ...` syntax does NOT route a named payload field named `inner` to
/// `Exception.InnerException`; only the constructor base-call
/// `Exception(message, inner)` does).
let private privateAssertCleanupInner (caught: exn) (simulated: exn) =
    let cleanup = caught :?> MigrationLockCleanupException

    Expect.equal
        cleanup.InnerException
        simulated
        "Cleanup exception carries the original ClearPool exception as its InnerException"

let tests =
    testList
        "Migration unlock-failure cleanup"
        [

          test
              "successful migration with deterministic unlock failure raises typed invariant, runs real ClearPool, ends the stale backend session" {
              // Acquire for real so the lock is genuinely held by the
              // session, then force release to report false so the
              // unlock-failure branch is exercised end-to-end.  The
              // runner must call the real `ClearPool` exactly once
              // and raise `MigrationInvariantException` with the
              // documented cleanup message.  The locked backend
              // session must end: its PID must be absent from
              // `pg_stat_activity` (verified with bounded polling)
              // and the advisory lock must be acquirable from a
              // fresh connection.
              let databaseName = $"circus_unlock_succ_{Guid.NewGuid():N}"
              let container = startIsolatedContainer databaseName
              let adminDataSource = adminDataSourceFor container

              let mutable acquiredPid = 0

              let ops, getClearCalls =
                  let mutable clearCalls = 0

                  let ops: Migration.MigrationLockOperations.Operations =
                      { TryAcquire =
                          fun (conn: NpgsqlConnection) ->
                              task {
                                  let! result = realTryAcquire conn

                                  if result then
                                      // Capture the locked
                                      // connection's backend PID at
                                      // the moment the lock is held
                                      // so the test can later
                                      // prove that physical session
                                      // is gone after ClearPool.
                                      acquiredPid <- conn.ProcessID

                                  return result
                              }
                        Release = fun (_: NpgsqlConnection) -> Task.FromResult false
                        ClearPool =
                          fun (conn: NpgsqlConnection) ->
                              clearCalls <- clearCalls + 1
                              realClearPool conn }

                  ops, (fun () -> clearCalls)

              try
                  let mutable caught: exn = null

                  try
                      Migration.migrateWithLockOperations ops adminDataSource |> waitUnit
                  with ex ->
                      caught <- ex

                  Expect.isNotNull caught "Migration.succeeded-with-unlock-failure is rejected with an exception"

                  Expect.equal
                      (caught.GetType())
                      (typeof<MigrationInvariantException>)
                      "Runner raises MigrationInvariantException when unlock fails after successful migration"

                  Expect.stringContains
                      caught.Message
                      "migration advisory lock could not be released"
                      "Runner surfaces the documented unlock-cleanup message"

                  // Real ClearPool was actually invoked.  The
                  // previous test seam substituted a no-op for the
                  // real pool clear and could not distinguish
                  // DISCARD ALL from ClearPool; the count assertion
                  // makes that distinction observable.
                  Expect.equal (getClearCalls ()) 1 "ClearPool is invoked exactly once on unlock failure"
                  Expect.isTrue (acquiredPid <> 0) "TryAcquire captured the locked backend PID"

                  // The locked backend session is gone after the
                  // runner returns: its PID is absent from
                  // pg_stat_activity.  Polled with a 5-second budget
                  // because Npgsql guarantees the connection is
                  // closed on return but does not guarantee another
                  // session's `pg_stat_activity` view is updated
                  // synchronously.
                  let stillActive = waitForPidDisappearance adminDataSource acquiredPid

                  Expect.isFalse
                      stillActive
                      "Locked backend session is no longer in pg_stat_activity after ClearPool (bounded 5s poll)"

                  // The next pooled acquisition acquires the
                  // migration advisory lock.  This proves the
                  // preceding ClearPool closed the stale physical
                  // session; without it, the next acquisition
                  // would either be served from the now-empty pool
                  // OR observe a stale advisory lock from a
                  // still-alive session.
                  use probeConn = adminDataSource.OpenConnection()
                  use probeLockCmd = probeConn.CreateCommand()
                  probeLockCmd.CommandText <- "SELECT pg_try_advisory_lock(@k)"
                  let kParam = probeLockCmd.CreateParameter()
                  kParam.ParameterName <- "k"
                  kParam.Value <- 0x43495243_55530001L
                  probeLockCmd.Parameters.Add(kParam) |> ignore
                  let lockResult = probeLockCmd.ExecuteScalar() |> unbox<bool>
                  Expect.isTrue lockResult "Subsequent pooled acquisition acquires the migration advisory lock"
              finally
                  adminDataSource.Dispose()
                  container.StopAsync() |> waitUnit
          }

          test
              "failed 000003 with deterministic unlock failure preserves the migration SQLSTATE and exact invariant message, leaves only 000003 unrecorded" {
              // The previous review flagged that pre-recording only
              // 000001 caused this test to assert "advancedCount = 0"
              // against a 000002 that the released 000002 never
              // tripped on: the released 000002 does not call
              // digest, so the digest-override fixture caused 000003
              // to fail instead - and 000003's failure was not the
              // SQLSTATE the test asserted.  This test uses the
              // released-parent fixture (000001 + 000002 already
              // recorded) and triggers a deterministic SQLSTATE
              // PZ001 on 000003 step 0b (the indirect-membership
              // membership violation) so the failure is the one the
              // test claims and the assertions are exact: the
              // SQLSTATE must equal `PZ001` and the server message
              // must equal the documented invariant message.
              let databaseName = $"circus_unlock_fail_{Guid.NewGuid():N}"
              let container = startIsolatedContainer databaseName
              let adminDataSource = adminDataSourceFor container

              try
                  applyReleasedParent adminDataSource

                  // Force 000003 step 0b to fail with the
                  // deterministic PZ001 indirect-membership
                  // invariant.  GRANT circus_owner TO
                  // circus_indirect and circus_indirect TO
                  // circus_app so the membership check
                  // (pg_has_role with MEMBER) fires after the
                  // runner's REVOKE removes the direct grant.
                  use prov = adminDataSource.OpenConnection()
                  use provCmd = prov.CreateCommand()

                  provCmd.CommandText <-
                      """
                      CREATE ROLE circus_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                      CREATE ROLE circus_indirect NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
                      GRANT circus_owner TO circus_indirect;
                      GRANT circus_indirect TO circus_app;
                      """

                  provCmd.ExecuteNonQuery() |> ignore

                  // The failed-migration test uses the real
                  // ClearPool seam (no counting wrapper) because its
                  // primary purpose is to verify the original
                  // PostgresException propagates verbatim.  Counting
                  // ClearPool would add little extra evidence here:
                  // the SQLSTATE / message / ledger assertions
                  // already prove the failed-migration branch was
                  // taken.
                  let ops: Migration.MigrationLockOperations.Operations =
                      { TryAcquire = realTryAcquire
                        Release = fun (_: NpgsqlConnection) -> Task.FromResult false
                        ClearPool = realClearPool }

                  let mutable caught: exn = null

                  try
                      Migration.migrateWithLockOperations ops adminDataSource |> waitUnit
                  with ex ->
                      caught <- ex

                  Expect.isNotNull caught "Failed 000003 with unlock failure surfaces an exception"

                  Expect.notEqual
                      (caught.GetType())
                      (typeof<MigrationInvariantException>)
                      "Original 000003 failure is not replaced by the unlock-cleanup invariant"

                  Expect.equal
                      (caught.GetType())
                      (typeof<PostgresException>)
                      "Original 000003 failure surfaces as a PostgresException"

                  let pgEx = caught :?> PostgresException

                  // The SQLSTATE is exact and stable: PZ001 is the
                  // ERRCODE raised by 000003 step 0b.
                  Expect.equal pgEx.SqlState "PZ001" "000003 raises the canonical PZ001 SQLSTATE"

                  // The server message is exact: Npgsql exposes
                  // `MessageText` as the primary PostgreSQL server
                  // message (distinct from `Message` which may
                  // include the SQLSTATE prefix).  Asserting on
                  // MessageText removes substring ambiguity.
                  Expect.equal
                      pgEx.MessageText
                      "migration_invariant: circus_app is a member of circus_owner (direct or indirect)"
                      "000003 message equals the exact invariant message verbatim"

                  // Only 000003 must be absent.  000001 and 000002
                  // were recorded by the released-parent fixture;
                  // the failed 000003 transaction rolled back via
                  // its own BEGIN/COMMIT, so the ledger does not
                  // advance past 000002.
                  use verifyConn = adminDataSource.OpenConnection()
                  use verifyCmd = verifyConn.CreateCommand()

                  verifyCmd.CommandText <-
                      "SELECT COUNT(*) FROM circus.circus_schema_migrations WHERE version = '000003_runtime_grant_hardening'"

                  let failedCount = verifyCmd.ExecuteScalar() |> unbox<int64>
                  Expect.equal failedCount 0L "Failed 000003 is not recorded as applied"
              finally
                  adminDataSource.Dispose()
                  container.StopAsync() |> waitUnit
          }

          test
              "successful migration with deterministic unlock failure leaves the cluster ready for a follow-up real migrate run" {
              // Stronger version of the stale-lock evidence: after a
              // forced unlock-failure run that exercises the real
              // ClearPool path (and counts the invocation), the
              // locked backend session is gone, the pool is empty,
              // and a follow-up Migration.migrate (with the real
              // lock operations) completes the same way a fresh
              // database would.  This proves the ClearPool fallback
              // ends the physical session that retained the
              // advisory lock and that the runner can recover
              // without operator intervention.
              let databaseName = $"circus_unlock_recover_{Guid.NewGuid():N}"
              let container = startIsolatedContainer databaseName
              let adminDataSource = adminDataSourceFor container

              try
                  let failingOps, getClearCalls = releaseFailsWithRealClearPool ()

                  let mutable caught: exn = null

                  try
                      Migration.migrateWithLockOperations failingOps adminDataSource |> waitUnit
                  with ex ->
                      caught <- ex

                  Expect.isNotNull caught "First migration with failing unlock reports cleanup failure"
                  Expect.equal (getClearCalls ()) 1 "First migration invoked ClearPool exactly once"

                  // Second run uses the production migration entry
                  // point with the real lock operations.
                  Migration.migrate adminDataSource |> waitUnit

                  use verifyConn = adminDataSource.OpenConnection()
                  use verifyCmd = verifyConn.CreateCommand()

                  verifyCmd.CommandText <-
                      "SELECT array_agg(version ORDER BY version) FROM circus.circus_schema_migrations"

                  let versions = verifyCmd.ExecuteScalar() |> string

                  Expect.stringContains
                      versions
                      "000003_runtime_grant_hardening"
                      "Recovery migration records 000003 after the unlock-failure cleanup cleared the pool"
              finally
                  adminDataSource.Dispose()
                  container.StopAsync() |> waitUnit
          }

          test
              "successful migration with deterministic unlock failure and a throwing ClearPool raises a typed cleanup exception with the original ClearPool exception as its InnerException" {
              // The previous review flagged that suppressing the
              // ClearPool failure and emitting the
              // "physical sessions were cleared" invariant when
              // ClearPool actually threw was a silent misreport.
              // This test exercises the (None, false, Some cleanup)
              // branch and asserts the runner surfaces
              // `MigrationLockCleanupException` carrying the
              // original ClearPool exception as its
              // `InnerException`.  The successful-migration
              // invariant message is preserved on the surface; the
              // inner exception names the cleanup failure.
              let databaseName = $"circus_unlock_cleanup_{Guid.NewGuid():N}"
              let container = startIsolatedContainer databaseName
              let adminDataSource = adminDataSourceFor container

              try
                  let simulated = InvalidOperationException "simulated ClearPool failure"

                  // The cleanup-exception test substitutes a
                  // throwing callback in place of the real
                  // ClearPool so the runner triggers the typed
                  // exception path.  A ClearPool call count here
                  // would always be 1 (the throwing call) but does
                  // not prove anything about the runner beyond
                  // what the exception type already proves.
                  let ops: Migration.MigrationLockOperations.Operations =
                      { TryAcquire = realTryAcquire
                        Release = fun (_: NpgsqlConnection) -> Task.FromResult false
                        ClearPool = fun (_: NpgsqlConnection) -> raise simulated }

                  let mutable caught: exn = null

                  try
                      Migration.migrateWithLockOperations ops adminDataSource |> waitUnit
                  with ex ->
                      caught <- ex

                  Expect.isNotNull caught "Runner surfaces an exception when unlock fails AND ClearPool throws"

                  Expect.equal
                      (caught.GetType())
                      (typeof<MigrationLockCleanupException>)
                      "Runner raises MigrationLockCleanupException when both unlock and ClearPool fail"

                  Expect.stringContains
                      caught.Message
                      "migration advisory lock could not be released"
                      "Cleanup exception surfaces the documented unlock-cleanup message"

                  privateAssertCleanupInner caught simulated
              finally
                  adminDataSource.Dispose()
                  container.StopAsync() |> waitUnit
          } ]
