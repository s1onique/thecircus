namespace Circus.Persistence.Postgres

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Npgsql

[<assembly: InternalsVisibleTo("Circus.Persistence.Postgres.Tests")>]
do ()

exception MigrationInvariantException of message: string

/// Raised when the migration advisory unlock reports `false` AND the
/// follow-up `ClearPool` call itself throws.  The successful-migration
/// path therefore surfaces the cleanup exception instead of swallowing
/// it and pretending the cleanup succeeded.  The inner exception is the
/// original `ClearPool` failure (typically an Npgsql / NpgsqlDataSource
/// pool-clear error).  On the failed-migration path the original
/// `PostgresException` still wins regardless of unlock outcome, so this
/// type is only ever raised when the migration body succeeded and the
/// runner is forced to fall back to `ClearPool`.
///
/// Implemented as a sealed class deriving from
/// `Exception(message, inner)` so that `InnerException` is wired
/// correctly.  The F# `exception ... of ...` syntax does NOT route a
/// named payload field of `inner` to `Exception.InnerException`; the
/// F# compiler simply lowers the payload to a constructor argument.
/// Only `Exception(message, inner)` ties the inner payload to
/// `InnerException`, so loggers, `GetBaseException()`, telemetry, and
/// the CA exception chain observe the causal exception.  See
/// https://github.com/fsharp/fslang-suggestions/issues/591 and the
/// F# language reference for Exception Types.
[<Sealed>]
type MigrationLockCleanupException(message: string, inner: exn) =
    inherit Exception(message, inner)

/// Canonical migration versions.  Each version is recorded in the
/// migration ledger verbatim and used to derive the embedded-resource
/// file name (version + ".sql").  Keeping the resource suffix out of the
/// ledger value lets the runner compare ledger rows to the canonical
/// version list directly.
module Migration =
    let private migrationVersions =
        [ "000001_event_journal"
          "000002_namespace_alignment"
          "000003_runtime_grant_hardening" ]

    /// Stable 64-bit advisory lock key derived from the canonical migration
    /// set.  Session-level advisory locks persist across transactions and
    /// are released only when the connection is physically closed or the
    /// lock is explicitly released.  The lock is taken before ledger
    /// discovery and held until every pending migration has either
    /// committed or rolled back.  Because the runner uses
    /// `pg_try_advisory_lock`, the lock produces fail-fast rejection of a
    /// concurrent runner rather than serialising it.
    let private migrationAdvisoryLockKey: int64 = 0x43495243_55530001L

    /// `pg_try_advisory_lock` returns `false` immediately when the lock is
    /// already held.  This is fail-fast rejection: a second runner that
    /// arrives while the first is in flight surfaces
    /// `MigrationInvariantException` rather than waiting.  The session
    /// advisory lock is released only by explicit `pg_advisory_unlock`,
    /// by physically ending the PostgreSQL session, or by the connector
    /// reset that Npgsql performs when the next connector is acquired;
    /// returning the pooled `NpgsqlConnection` to the pool on `use` does
    /// NOT release the lock.
    let private tryAcquireMigrationLock (conn: NpgsqlConnection) : Task<bool> =
        task {
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT pg_try_advisory_lock(@k)"
            let p = cmd.CreateParameter()
            p.ParameterName <- "k"
            p.Value <- migrationAdvisoryLockKey
            cmd.Parameters.Add(p) |> ignore
            let! result = cmd.ExecuteScalarAsync()
            return unbox<bool> result
        }

    /// Real PostgreSQL release: best-effort `pg_advisory_unlock`.  Returns
    /// `false` (never raises) when the session still holds the lock or
    /// when the unlock command itself fails.  `MigrationLockOperations`
    /// below exposes this seam to the test assembly only.
    let private releaseMigrationLockImpl (conn: NpgsqlConnection) : Task<bool> =
        task {
            try
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "SELECT pg_advisory_unlock(@k)"
                let p = cmd.CreateParameter()
                p.ParameterName <- "k"
                p.Value <- migrationAdvisoryLockKey
                cmd.Parameters.Add(p) |> ignore
                let! result = cmd.ExecuteScalarAsync()
                return unbox<bool> result
            with _ ->
                return false
        }

    /// Internal abstraction over the advisory-lock operations.  The
    /// production path uses `MigrationLockOperations.real`; the
    /// persistence test assembly injects deterministic release failure
    /// so the unlock-failure branches can be exercised end-to-end.
    /// The whole `MigrationLockOperations` module is internal so that
    /// the migration authority remains a single public entry point
    /// (`Migration.migrate`); a test-only fixture executes through
    /// `Migration.migrateWithLockOperations` rather than re-entering
    /// the runner.
    ///
    /// `ClearPool` is intentionally synchronous.  `NpgsqlConnection.ClearPool`
    /// (and the data-source `NpgsqlDataSource.Clear()` it delegates to)
    /// return `unit` and never block on a remote resource; there is
    /// nothing to `await`.  Only rollback and the advisory unlock are
    /// asynchronous, because the underlying PostgreSQL commands travel
    /// over a network connection.  Treating `ClearPool` as if it were
    /// awaited would force a redundant `Task.RunSynchronously` or
    /// `Async.RunSynchronously` call and reintroduce exactly the
    /// sync-over-async defect the runner is designed to avoid.
    module internal MigrationLockOperations =
        type Operations =
            { TryAcquire: NpgsqlConnection -> Task<bool>
              Release: NpgsqlConnection -> Task<bool>
              ClearPool: NpgsqlConnection -> unit }

        let real: Operations =
            { TryAcquire = tryAcquireMigrationLock
              Release = releaseMigrationLockImpl
              ClearPool = fun (conn: NpgsqlConnection) -> NpgsqlConnection.ClearPool(conn) }

    let private resourceName (version: string) = version + ".sql"

    let private readMigration (version: string) =
        let name = resourceName version
        let assembly = Assembly.GetExecutingAssembly()

        let resourceName' =
            assembly.GetManifestResourceNames()
            |> Array.tryFind (fun candidate -> candidate.EndsWith(name, StringComparison.Ordinal))

        match resourceName' with
        | Some resource ->
            use stream = assembly.GetManifestResourceStream(resource)
            use reader = new StreamReader(stream)
            reader.ReadToEnd()
        | None ->
            let candidates =
                [ Path.Combine(AppContext.BaseDirectory, "db", "migrations", name)
                  Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "db", "migrations", name)
                  Path.Combine(Directory.GetCurrentDirectory(), "db", "migrations", name) ]

            match candidates |> List.tryFind File.Exists with
            | Some path -> File.ReadAllText path
            | None -> failwith "Circus database migration resource is missing"

    /// Discover which ledger table exists on the supplied connection.
    /// Takes the locked connection so the runner does not need to open
    /// a second pool slot while the advisory lock is held.
    let private ledgerTableOnConnection (conn: NpgsqlConnection) : Task<string> =
        task {
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                """
                SELECT
                  (SELECT EXISTS (SELECT 1 FROM pg_tables
                                   WHERE schemaname = 'public'
                                     AND tablename   = 'circus_schema_migrations')),
                  (SELECT EXISTS (SELECT 1 FROM pg_tables
                                   WHERE schemaname = 'circus'
                                     AND tablename   = 'circus_schema_migrations'))
                """

            use! reader = cmd.ExecuteReaderAsync()
            let! _ = reader.ReadAsync()
            let hasPublic = reader.GetBoolean(0)
            let hasCircus = reader.GetBoolean(1)

            if hasPublic && hasCircus then
                raise
                <| MigrationInvariantException
                    "ambiguous migration ledger: both public.circus_schema_migrations and circus.circus_schema_migrations exist"

            if hasCircus then return "circus.circus_schema_migrations"
            elif hasPublic then return "public.circus_schema_migrations"
            else return ""
        }

    let private readVersionsOnConnection (conn: NpgsqlConnection) (source: string) : Task<string list> =
        // Returns ledger rows in recorded time order so the validator can
        // detect an out-of-order history (for example, 000002 recorded
        // before 000001).  PostgreSQL does not expose insertion order;
        // `applied_at` is the only historical evidence available in the
        // released ledger schema.
        //
        // `applied_at` is a wall-clock timestamp with finite resolution.
        // If two ledger rows share the same value, their historical order
        // cannot be reconstructed.  The runner therefore fails closed
        // instead of using `version` as a deterministic but invented
        // history tie-breaker.  A deterministic sort would make the
        // validator accept an ordering for which the ledger contains no
        // evidence.
        //
        // The ordered-list return preserves duplicate and out-of-order
        // signals for the canonical-prefix validator.  The runner compares
        // the recorded list against the canonical migration list with strict
        // positional equality; an out-of-order row, a duplicate, a gap, an
        // extra unknown row, or an ambiguous timestamp all fail closed.
        task {
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                sprintf
                    "SELECT version, count(*) OVER (PARTITION BY applied_at) AS timestamp_count FROM %s ORDER BY applied_at ASC"
                    source

            use! reader = cmd.ExecuteReaderAsync()
            let mutable versions = ResizeArray<string>()

            while reader.Read() do
                let timestampCount = reader.GetInt64(1)

                if timestampCount > 1L then
                    raise
                    <| MigrationInvariantException
                        "non-canonical migration ledger: multiple versions share the same applied_at; historical order cannot be reconstructed"

                versions.Add(reader.GetString(0))

            return List.ofSeq versions
        }

    /// Format a string list as `[a; b; c]` so the canonical-prefix
    /// violation messages are deterministic and easy to read in the
    /// PostgreSQL server output.
    let private formatVersionList (versions: string list) : string =
        sprintf "[%s]" (String.concat "; " versions)

    /// Validate the ledger as a canonical prefix of the migration list.
    /// Reject, in order:
    /// * unknown versions (any row whose value is not in the canonical list);
    /// * duplicate versions (the same canonical name recorded twice);
    /// * gaps (e.g. `[000001, 000003]` is not a prefix of the canonical list);
    /// * out-of-order suffixes (e.g. `[000002]` without `000001`);
    /// * ledgers longer than the canonical list.
    /// Accept exactly:
    ///   []
    ///   [000001]
    ///   [000001, 000002]
    ///   [000001, 000002, 000003]
    ///
    /// The validator operates on an ordered list (not a set) so the
    /// duplicate and out-of-order signals survive the discovery step;
    /// the previous round's `Set<string>` return silently merged both.
    let private validateCanonicalPrefix (applied: string list) : unit =
        let known = Set.ofList migrationVersions

        // 1. Reject any row whose value is not a canonical migration
        //    name.  Without this check, a stray ledger row could
        //    pollute the prefix comparison and mask a real gap.
        for version in applied do
            if not (Set.contains version known) then
                raise
                <| MigrationInvariantException $"non-canonical migration ledger: unknown version '{version}'"

        // 2. Reject duplicate ledger rows.  The early round used a
        //    `Set<string>` return type, which silently collapsed
        //    every duplicate into a single representative; the
        //    validator could not distinguish a one-row ledger from
        //    a duplicate-row ledger of the same canonical name.
        let mutable seen = Set.empty

        for version in applied do
            if Set.contains version seen then
                raise
                <| MigrationInvariantException $"non-canonical migration ledger: duplicate version '{version}'"

            seen <- Set.add version seen

        // 3. Reject any ledger that is not a contiguous prefix of the
        //    canonical migration list.  Equivalently:
        //    applied[i] = migrationVersions[i] for every index
        //    0 <= i < applied.Length, and applied.Length <=
        //    migrationVersions.Length.  The equality compare means a
        //    gap (e.g. `[000001, 000003]`) or an out-of-order suffix
        //    (e.g. `[000002]` without `000001`) is rejected because
        //    either length-checked above or character-comparison
        //    inside the loop.
        if List.length applied > List.length migrationVersions then
            raise
            <| MigrationInvariantException
                $"non-canonical migration ledger: applied ledger {formatVersionList applied} has more versions than the canonical migration list {formatVersionList migrationVersions}"

        let appliedArr = List.toArray applied
        let canonicalArr = List.toArray migrationVersions

        for i in 0 .. appliedArr.Length - 1 do
            if appliedArr.[i] <> canonicalArr.[i] then
                raise
                <| MigrationInvariantException
                    $"non-canonical migration ledger: versions {formatVersionList applied} are not a contiguous prefix of {formatVersionList migrationVersions}"

    /// Discover which versions are already applied, in recorded order.
    /// Operates on the supplied locked connection so the runner does
    /// not consume a second pool slot while the advisory lock is held.
    /// Treats the legacy `public.circus_schema_migrations` ledger and
    /// the canonical `circus.circus_schema_migrations` ledger as
    /// disjoint authoritative sources.  If both exist, the migration
    /// state is ambiguous and the runner fails explicitly.  Returns an
    /// ordered `string list` (not a `Set<string>`) so the validator
    /// can observe duplicates and ordering.
    let private discoverAppliedOnConnection (conn: NpgsqlConnection) : Task<string list> =
        task {
            let! table = ledgerTableOnConnection conn

            let! versions =
                if String.IsNullOrEmpty table then
                    Task.FromResult []
                else
                    readVersionsOnConnection conn table

            return versions
        }

    /// Apply one migration as a single atomic unit.  Each migration file
    /// owns its own BEGIN/COMMIT so a failure inside the SQL rolls the
    /// version insertion back and the version is not recorded as applied.
    /// The runner does NOT blanket-execute the migration as `circus_owner`:
    /// role creation, ALTER ROLE, role membership, and CREATE EXTENSION are
    /// all administrator operations that require the connection user to be
    /// able to SET ROLE or hold CREATEDB / CREATEROLE.  Future migrations
    /// that genuinely need `circus_owner` as the creator role must
    /// `SET ROLE circus_owner` only inside the narrow statements that
    /// require it; the creator-role probe is exercised in CORRECTION03.
    let private applyMigration (conn: NpgsqlConnection) (version: string) : Task<unit> =
        task {
            let sql = readMigration version
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
        }

    /// Restore the connection to a usable state after an aborted
    /// transaction.  Best-effort: any error during the rollback is
    /// suppressed so the original migration error can be rethrown.
    /// Used only on the migration-failure path; on the success path
    /// the migration body owns its own transaction and the runner
    /// does not need a follow-up `ROLLBACK`.
    let private rollbackIfAborted (conn: NpgsqlConnection) : Task<unit> =
        task {
            try
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "ROLLBACK"
                do! cmd.ExecuteNonQueryAsync() |> Async.AwaitTask |> Async.Ignore
            with _ ->
                ()
        }

    /// Apply the canonical migrations.  Holds the session advisory lock
    /// for the duration of the run so concurrent callers cannot race
    /// through the check-then-create constructs.  Ledger discovery runs
    /// on the same locked connection so the runner does not consume a
    /// second pool slot while the lock is held.  Every pending migration
    /// runs as the connection's administrator (the migration user); the
    /// runner does NOT blanket-execute migrations as `circus_owner`
    /// because that role does not exist on fresh or released-parent
    /// databases, and even when it does exist it is intentionally
    /// `NOCREATEROLE NOSUPERUSER` and cannot ALTER ROLE other roles or
    /// install extensions into a postgres-owned database.  On a
    /// migration SQL failure the connection is rolled back to clear the
    /// aborted state before the advisory lock is released; the original
    /// `PostgresException` propagates to the caller.
    ///
    /// Unlock-failure policy:
    /// * Migration succeeded, unlock succeeded: return normally.
    /// * Migration succeeded, unlock failed, pool clear succeeded:
    ///   raise `MigrationInvariantException` reporting that the advisory
    ///   lock could not be released and that the physical-session
    ///   cleanup was requested.
    /// * Migration succeeded, unlock failed, pool clear threw:
    ///   raise `MigrationLockCleanupException` carrying the
    ///   `ClearPool` exception as its `InnerException`.  The runner
    ///   does NOT silently claim the physical sessions were cleared
    ///   when the cleanup itself errored.
    /// * Migration failed (regardless of unlock outcome): the original
    ///   migration exception is rethrown verbatim via
    ///   `ExceptionDispatchInfo`.  A failed unlock does not replace
    ///   the original failure, and a `ClearPool` throw on this path
    ///   is intentionally swallowed so the original exception is
    ///   authoritative.
    ///
    /// Cleanup ordering: the migration loop's exception is captured,
    /// the connection is rolled back (only on the migration-failure
    /// path), the advisory lock release is awaited, and only then is
    /// the pool optionally cleared.  No `Async.RunSynchronously`,
    /// `Task.Wait`, or `.Result` access appears on the production
    /// task path.  `ClearPool` is a synchronous Npgsql API and is
    /// therefore invoked directly rather than awaited; the rollback
    /// and unlock steps are awaited with `do!` / `let!` because the
    /// underlying PostgreSQL calls travel over a network connection.
    ///
    /// Cancellation: the migration API accepts no `CancellationToken`
    /// and the Npgsql calls use their default token.  If an awaited
    /// Npgsql operation raises `OperationCanceledException` mid-run,
    /// the runner captures it through `ExceptionDispatchInfo`, performs
    /// best-effort cleanup, and rethrows the same cancellation
    /// exception.  This is incidental cancellation propagation, not
    /// caller-driven cancellation control.
    let internal migrateWithLockOperations
        (ops: MigrationLockOperations.Operations)
        (dataSource: NpgsqlDataSource)
        : Task<unit> =
        task {
            use! conn = dataSource.OpenConnectionAsync()

            let! acquired = ops.TryAcquire conn

            if not acquired then
                raise
                <| MigrationInvariantException "another migration runner is already active for this database"

            // Linear async cleanup: capture the original failure,
            // await the rollback to clear the aborted transaction
            // state, then await the advisory-lock release.  The
            // `ExceptionDispatchInfo` is preserved verbatim so the
            // caller's `PostgresException` (or any other typed
            // migration failure) survives the cleanup round-trip.
            let mutable captured: ExceptionDispatchInfo option = None

            try
                let! applied = discoverAppliedOnConnection conn
                validateCanonicalPrefix applied

                // `validateCanonicalPrefix` guarantees that
                // `applied` is exactly the longest contiguous prefix
                // of `migrationVersions`.  We therefore apply only
                // the suffix that follows the recorded prefix: skip
                // the first `applied.Length` entries.  The
                // index-based loop is the canonical-prefix
                // counterpart of the previous `Set.contains` loop
                // and avoids re-checking membership per iteration.
                let pending = List.skip applied.Length migrationVersions

                for version in pending do
                    do! applyMigration conn version
            with original ->
                do! rollbackIfAborted conn
                captured <- Some(ExceptionDispatchInfo.Capture original)

            let! unlocked = ops.Release conn

            // Best-effort physical-session cleanup when the session
            // still holds the migration advisory lock.  `ClearPool` is
            // synchronous; we capture any exception it throws so the
            // final match expression can distinguish "cleanup cleared
            // the pool" from "cleanup also threw".
            let clearFailure: exn option =
                if unlocked then
                    None
                else
                    try
                        ops.ClearPool conn
                        None
                    with clearEx ->
                        Some clearEx

            match captured, unlocked, clearFailure with
            | Some original, _, _ ->
                // Original migration failure wins regardless of unlock
                // outcome.  `Throw` rethrows the captured exception
                // and propagates it through the Task.
                original.Throw()
            | None, false, None ->
                // Migration succeeded, unlock failed, pool clear
                // succeeded.  The physical session that retained the
                // advisory lock has been marked for closure and the
                // idle physical sessions in the pool have been
                // closed; future pooled acquisitions cannot observe
                // the stale lock.
                raise
                <| MigrationInvariantException
                    "migration advisory lock could not be released; pool clearing was requested"
            | None, false, Some cleanup ->
                // Migration succeeded, unlock failed, AND pool clear
                // itself threw.  The successful-migration path must
                // surface the cleanup failure rather than silently
                // reporting that the physical sessions were cleared.
                raise
                <| MigrationLockCleanupException(
                    "migration advisory lock could not be released; physical-session cleanup itself failed",
                    cleanup
                )
            | None, true, _ -> ()
        }

    /// Production migration entry point.  Delegates to
    /// `migrateWithLockOperations` with the real PostgreSQL lock
    /// implementation so the public surface remains a single
    /// authoritative call.
    let migrate (dataSource: NpgsqlDataSource) : Task<unit> =
        migrateWithLockOperations MigrationLockOperations.real dataSource
