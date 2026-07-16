-- 000003_runtime_grant_hardening
--
-- Self-sufficient corrective migration that carries the full set of
-- post-000002 schema, ownership, digest, trigger, index, and runtime
-- hardening.  The existing 000002_namespace_alignment is restored to its
-- released byte-for-byte contents; this migration is the only path
-- through which an environment that already recorded
-- 000002_namespace_alignment receives the corrected invariants.
--
-- Every statement is idempotent.  The migration is independent of any
-- future migration; it does not depend on 000001 or 000002 having been
-- rewritten, only on their canonical version names being recorded in
-- circus.circus_schema_migrations.
--
-- The released 000001_event_journal created only circus_app and never
-- created circus_owner.  The released 000002_namespace_alignment does
-- not reconcile roles.  Therefore 000003 must reconcile BOTH roles
-- before referencing either one.  This migration's first executable
-- step is the role-reconciliation block; the schema and extension
-- blocks that follow assume circus_owner exists.
--
-- The released 000002 also authored a `raw_body_sha256` length-only
-- CHECK (NULL OR octet_length = 32).  A database that ran the released
-- 000002 retains that named constraint on the same column that 000003
-- must enforce with an equality CHECK.  PostgreSQL constraint names are
-- unique per table, so 000003 must drop every legacy digest-related
-- CHECK before re-authoring the canonical one.  The migration uses a
-- catalog-driven drop (see step 7b) so that a future released
-- constraint with a different name does not block the upgrade either.
BEGIN;

-- 0.  Role reconciliation.  circus_owner and circus_app must exist
--     before the extension schema, the circus.* ownership transfers,
--     the runtime grants, and the default privileges.  The released
--     000001 created only circus_app; this block is therefore the
--     first executable hardening step.  Idempotent: existing roles
--     are re-stated with the canonical flags.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'circus_owner') THEN
        CREATE ROLE circus_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
    ELSE
        ALTER ROLE circus_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'circus_app') THEN
        CREATE ROLE circus_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
    ELSE
        ALTER ROLE circus_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
    END IF;
END
$$;

-- 0b.  Role membership reconciliation.  ALTER ROLE cannot modify role
--     memberships; NOINHERIT does not prevent SET ROLE escalation through
--     a direct or indirect membership path.  Revoke every circus_owner
--     membership held by circus_app and fail closed if any direct or
--     indirect path remains (pg_has_role with MEMBER detects direct and
--     indirect grants; pg_has_role with SET detects the SET ROLE
--     escalation path that NOINHERIT does not cover).
DO $$
BEGIN
    EXECUTE format('REVOKE %I FROM %I', 'circus_owner', 'circus_app');

    IF pg_has_role('circus_app', 'circus_owner', 'MEMBER') THEN
        RAISE EXCEPTION USING
            ERRCODE = 'PZ001',
            MESSAGE = 'migration_invariant: circus_app is a member of circus_owner (direct or indirect)';
    END IF;

    IF pg_has_role('circus_app', 'circus_owner', 'SET') THEN
        RAISE EXCEPTION USING
            ERRCODE = 'PZ001',
            MESSAGE = 'migration_invariant: circus_app can SET ROLE circus_owner';
    END IF;
END
$$;

-- 1.  Extension schema.  Install extensions only into a trusted,
--     locked schema whose `CREATE` privilege is held by the migration
--     authority alone.  This prevents a privileged unqualified lookup
--     from creating a search-path substitution risk from an untrusted
--     schema.  Roles are reconciled in step 0, so circus_owner exists
--     here.  When the schema already exists, fail closed on an
--     unexpected owner or an unexpected CREATE grant.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'circus_extensions') THEN
        CREATE SCHEMA circus_extensions AUTHORIZATION circus_owner;
    ELSE
        IF (SELECT pg_get_userbyid(nspowner)
              FROM pg_namespace
             WHERE nspname = 'circus_extensions')
           IS DISTINCT FROM 'circus_owner' THEN
            RAISE EXCEPTION USING
                ERRCODE = 'PZ001',
                MESSAGE = 'migration_invariant: circus_extensions has unexpected owner';
        END IF;

        IF EXISTS (
            SELECT 1
              FROM pg_catalog.aclexplode(
                  coalesce(
                      (SELECT nspacl FROM pg_namespace WHERE nspname = 'circus_extensions'),
                      pg_catalog.acldefault(
                          'n',
                          (SELECT nspowner FROM pg_namespace WHERE nspname = 'circus_extensions')
                      )
                  )
              ) AS acl
             WHERE acl.privilege_type = 'CREATE'
               AND acl.grantee <> (SELECT oid FROM pg_roles WHERE rolname = 'circus_owner')
        ) THEN
            RAISE EXCEPTION USING
                ERRCODE = 'PZ001',
                MESSAGE = 'migration_invariant: circus_extensions has unexpected CREATE grants';
        END IF;
    END IF;

    REVOKE ALL ON SCHEMA circus_extensions FROM PUBLIC, circus_app;
END
$$;

-- 2.  Install pgcrypto into the locked extension schema.  Use IF NOT
--     EXISTS so the migration is idempotent.  A pre-existing pgcrypto
--     in another schema is detected and the migration reports a
--     fail-closed invariant; we do not silently rely on the session
--     search path.
DO $$
DECLARE
    ext_schema text;
BEGIN
    SELECT n.nspname
      INTO ext_schema
      FROM pg_extension e
      JOIN pg_namespace n ON n.oid = e.extnamespace
     WHERE e.extname = 'pgcrypto';

    IF ext_schema IS NULL THEN
        CREATE EXTENSION pgcrypto WITH SCHEMA circus_extensions;
    ELSIF ext_schema <> 'circus_extensions' THEN
        RAISE EXCEPTION
            'migration_invariant: pgcrypto is installed in schema % (not circus_extensions); reconcile out of band',
            ext_schema;
    END IF;
END
$$;

-- 3.  Canonical schema ownership.  The released 000002 created the
--     circus schema; this migration restates the owner idempotently.
ALTER SCHEMA circus OWNER TO circus_owner;

-- 4.  Reject explicit ambiguous dual-schema state inside the canonical
--     namespace.  The migration authorises a single authority per
--     table; any database with both `public.circus_event_journal` and
--     `circus.circus_event_journal` (or the other application objects)
--     must be reconciled out of band.
DO $$
BEGIN
    IF to_regclass('public.circus_event_journal') IS NOT NULL
       AND to_regclass('circus.circus_event_journal') IS NOT NULL THEN
        RAISE EXCEPTION 'migration_invariant: both public.circus_event_journal and circus.circus_event_journal exist';
    END IF;
    IF to_regclass('public.circus_run_projection') IS NOT NULL
       AND to_regclass('circus.circus_run_projection') IS NOT NULL THEN
        RAISE EXCEPTION 'migration_invariant: both public.circus_run_projection and circus.circus_run_projection exist';
    END IF;
    IF to_regclass('public.circus_schema_migrations') IS NOT NULL
       AND to_regclass('circus.circus_schema_migrations') IS NOT NULL THEN
        RAISE EXCEPTION 'migration_invariant: both public.circus_schema_migrations and circus.circus_schema_migrations exist';
    END IF;
END
$$;

-- 5.  Ownership.  Tables, sequence, ledger, trigger function all
--     transferred to circus_owner.  Re-asserted idempotently.
ALTER TABLE circus.circus_event_journal OWNER TO circus_owner;
ALTER TABLE circus.circus_run_projection OWNER TO circus_owner;
ALTER TABLE circus.circus_schema_migrations OWNER TO circus_owner;
ALTER SEQUENCE circus.circus_event_journal_journal_position_seq OWNER TO circus_owner;

-- 6.  Drop the existing append-only UPDATE/DELETE triggers BEFORE the
--     digest-repair UPDATE.  The triggers were created by 000001 and
--     remain associated with the table across 000002.  If the backfill
--     UPDATE fires them, the transaction rolls back and 000003 is
--     never recorded in the ledger.
DROP TRIGGER IF EXISTS circus_event_journal_prevent_update
    ON circus.circus_event_journal;
DROP TRIGGER IF EXISTS circus_event_journal_prevent_delete
    ON circus.circus_event_journal;
DROP FUNCTION IF EXISTS circus.prevent_journal_modification();

-- 7.  Authoritative raw-digest invariant.  Repair every existing
--     row, then drop every legacy digest-related CHECK constraint,
--     then re-author the canonical equality constraint, then forbid
--     NULL.  The CHECK drop is catalog-driven: every CHECK whose
--     `consrc` references `raw_body_sha256` is dropped before the
--     canonical constraint is re-authored.  This protects future
--     upgrades against any new legacy constraint name authored by a
--     released migration.  Idempotent: the UPDATE is a no-op when
--     digests already match.  Uses the schema-qualified digest()
--     from circus_extensions.
UPDATE circus.circus_event_journal
   SET raw_body_sha256 = circus_extensions.digest(raw_body, 'sha256')
 WHERE raw_body_sha256 IS DISTINCT FROM circus_extensions.digest(raw_body, 'sha256');

-- 7b. Catalog-driven removal of every legacy CHECK that references
--     `raw_body_sha256`.  The released 000002 authored a length-only
--     CHECK with the same name as the canonical one; a future
--     released migration could author a different-named CHECK that
--     still references the digest column.  `conkey` is inspected
--     through `pg_get_constraintdef(c.oid)` so we do not depend on
--     `consrc` (removed in PostgreSQL 12+).  Idempotent: nothing is
--     dropped if no legacy CHECK exists.
DO $$
DECLARE
    legacy_constraint text;
BEGIN
    FOR legacy_constraint IN
        SELECT c.conname
          FROM pg_constraint c
          JOIN pg_class t ON t.oid = c.conrelid
          JOIN pg_namespace n ON n.oid = t.relnamespace
         WHERE n.nspname = 'circus'
           AND t.relname = 'circus_event_journal'
           AND c.contype = 'c'
           AND pg_get_constraintdef(c.oid) ILIKE '%raw_body_sha256%'
    LOOP
        EXECUTE format(
            'ALTER TABLE circus.circus_event_journal DROP CONSTRAINT %I',
            legacy_constraint
        );
    END LOOP;
END
$$;

-- The canonical raw-digest invariant: every persisted row has a
-- 32-byte SHA-256 digest of its raw body.  Uses the schema-qualified
-- digest() so a future change to the session search path cannot
-- silently alter the resolved function.
ALTER TABLE circus.circus_event_journal
    ADD CONSTRAINT circus_event_journal_raw_body_sha256_ck
    CHECK (raw_body_sha256 = circus_extensions.digest(raw_body, 'sha256'));

-- Forbid NULL digests.  `NOT NULL` is the only mechanism that
-- guarantees the column carries a digest on every row.
ALTER TABLE circus.circus_event_journal
    ALTER COLUMN raw_body_sha256 SET NOT NULL;

-- 8.  Re-create the append-only trigger function and triggers.  The
--     trigger function is created by the migration authority under
--     circus_owner with the canonical search_path and SECURITY
--     INVOKER settings.  Re-created after the digest hardening so they
--     protect the canonical state from any subsequent UPDATE or
--     DELETE even by the runtime role.
CREATE OR REPLACE FUNCTION circus.prevent_journal_modification()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog, circus
AS $$
BEGIN
    RAISE EXCEPTION 'journal is append-only';
END;
$$;
ALTER FUNCTION circus.prevent_journal_modification() OWNER TO circus_owner;

CREATE TRIGGER circus_event_journal_prevent_update
    BEFORE UPDATE ON circus.circus_event_journal
    FOR EACH ROW EXECUTE FUNCTION circus.prevent_journal_modification();
CREATE TRIGGER circus_event_journal_prevent_delete
    BEFORE DELETE ON circus.circus_event_journal
    FOR EACH ROW EXECUTE FUNCTION circus.prevent_journal_modification();

-- 9.  Indexes.  Always circus-qualified, never in public.
CREATE INDEX IF NOT EXISTS circus_event_journal_run_id_position_idx
    ON circus.circus_event_journal (run_id, journal_position);
CREATE INDEX IF NOT EXISTS circus_event_journal_event_type_position_idx
    ON circus.circus_event_journal (event_type, journal_position);
CREATE INDEX IF NOT EXISTS circus_event_journal_received_at_position_idx
    ON circus.circus_event_journal (received_at, journal_position);

-- 10a. Revoke CREATE on the public schema from PUBLIC and from
--      circus_app.  PostgreSQL warns that an upgraded cluster can
--      retain PUBLIC CREATE on the public schema, which lets any role
--      install objects that shadow untrusted lookup.  Fresh PostgreSQL
--      15+ no longer grants CREATE on public to PUBLIC by default,
--      but released and legacy environments may have a different ACL.
--      Revoke idempotently and assert the resulting state.
DO $$
BEGIN
    EXECUTE 'REVOKE CREATE ON SCHEMA public FROM PUBLIC';
    EXECUTE 'REVOKE CREATE ON SCHEMA public FROM circus_app';

    IF EXISTS (
        SELECT 1
          FROM pg_catalog.pg_namespace n
          CROSS JOIN LATERAL pg_catalog.aclexplode(
              coalesce(
                  n.nspacl,
                  pg_catalog.acldefault('n', n.nspowner)
              )
          ) AS acl
         WHERE n.nspname = 'public'
           AND acl.grantee = 0::oid
           AND acl.privilege_type = 'CREATE'
    ) THEN
        RAISE EXCEPTION USING
            ERRCODE = 'PZ001',
            MESSAGE = 'migration_invariant: PUBLIC retains CREATE on schema public';
    END IF;

    IF has_schema_privilege('circus_app', 'public', 'CREATE') THEN
        RAISE EXCEPTION USING
            ERRCODE = 'PZ001',
            MESSAGE = 'migration_invariant: circus_app retains CREATE on schema public';
    END IF;
END
$$;

-- 10b. Revoke inherited and PUBLIC privileges.  The migration authority
--      must run this AFTER every grant-producing statement.
REVOKE ALL PRIVILEGES ON SCHEMA circus FROM PUBLIC, circus_app;
REVOKE ALL PRIVILEGES ON TABLE circus.circus_event_journal FROM PUBLIC, circus_app;
REVOKE ALL PRIVILEGES ON TABLE circus.circus_run_projection FROM PUBLIC, circus_app;
REVOKE ALL PRIVILEGES ON TABLE circus.circus_schema_migrations FROM PUBLIC, circus_app;
REVOKE ALL PRIVILEGES ON SEQUENCE circus.circus_event_journal_journal_position_seq FROM PUBLIC, circus_app;
REVOKE ALL PRIVILEGES ON FUNCTION circus.prevent_journal_modification() FROM PUBLIC, circus_app;

-- 11. Narrow runtime grants.  The journal is append-only at the
--     application level (the trigger above rejects UPDATE and DELETE
--     even for the runtime role); the journal therefore does not need
--     an UPDATE grant.  The projection requires UPDATE because
--     ProjectionRepository performs an upsert.
GRANT USAGE ON SCHEMA circus TO circus_app;
GRANT SELECT, INSERT
    ON TABLE circus.circus_event_journal
    TO circus_app;
GRANT USAGE, SELECT
    ON SEQUENCE circus.circus_event_journal_journal_position_seq
    TO circus_app;
GRANT SELECT, INSERT, UPDATE
    ON TABLE circus.circus_run_projection
    TO circus_app;

-- 12. Effective default privileges for `circus_owner`.  These
--     `ALTER DEFAULT PRIVILEGES` statements configure the canonical
--     future object-creating role so that future tables, sequences,
--     and functions it creates have PUBLIC grants revoked by default.
--     No actual `SET ROLE circus_owner` migration path exists in this
--     ACT; the statements here are an idempotent declaration of the
--     future creator role's default-privilege policy and take effect
--     on the next migration that runs `SET ROLE circus_owner` inside
--     the narrow object-only statements.
--
--     PostgreSQL combines default privilege entries: schema-scoped
--     entries (with `IN SCHEMA`) union with the role-wide entries
--     (no `IN SCHEMA`) for the matching object kind, and the union
--     applies to every new object of that kind that the role
--     creates.  Setting only the schema-scoped entry therefore leaves
--     a global-default gap: a future object the creator role creates
--     in another schema falls through to whatever global defaults
--     exist, which may include a pre-recorded grant to PUBLIC from
--     an earlier or third-party release.  The migration must declare
--     both the role-wide defaults AND the schema-scoped defaults so
--     that the union is the canonical "PUBLIC has no grants"
--     regardless of where the creator role's future objects live.
--
--     The `IN SCHEMA circus` revokes are operational normalization,
--     not documentation alone.  A role-wide revoke removes the
--     matching global default, but it cannot remove an independently
--     stored schema-specific grant.  Conversely, a schema-specific
--     function revoke cannot remove PostgreSQL's role-wide PUBLIC
--     EXECUTE default for newly-created functions.  Both scopes must
--     therefore be revoked for functions as well as for tables and
--     sequences: the global function revoke removes the hard-wired /
--     role-wide default and the `IN SCHEMA circus` function revoke
--     removes any prior per-schema PUBLIC grant.
ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner
    REVOKE ALL ON TABLES FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner
    REVOKE ALL ON SEQUENCES FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner
    IN SCHEMA circus
    REVOKE ALL ON TABLES FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner
    IN SCHEMA circus
    REVOKE ALL ON SEQUENCES FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner
    IN SCHEMA circus
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

-- 12b. Catalog-driven assertion that no default-privilege entry for
--      circus_owner leaves an effective grant to PUBLIC for tables,
--      sequences, or functions in any schema.  `pg_default_acl`
--      stores one row per role/scope pair, with `defaclacl` carrying
--      the granted (positive) ACL.  We assert that for the three
--      object kinds, the union of all default ACLs whose grantor is
--      circus_owner contains no PUBLIC entry.  PUBLIC is not a
--      stored role row.  `pg_catalog.aclexplode` represents the
--      PUBLIC pseudo-role with `grantee = 0`, so the assertion
--      compares `acl.grantee` with `0::oid` rather than a regex
--      against the textual ACL (which would be brittle and
--      locale-dependent).  A `REVOKE ALL` may leave the
--      grantee slot present in
--      `defaclacl` with an empty privilege set rather than removing
--      the entry entirely; the filter on `acl.privilege_type <> ''`
--      skips those fully-revoked entries and only flags a real
--      PUBLIC hold.  This catches a future divergent default that,
--      combined with the union semantics, could re-grant PUBLIC
--      something even though the migration itself revoked it.
--      Idempotent: the DO block inspects the catalog only and writes
--      nothing.
DO $$
DECLARE
    bad_kind text;
BEGIN
    -- `pg_default_acl.defaclobjtype` is a single-character code:
    -- 'r' = relation, 'S' = sequence, 'f' = function (also covers
    -- procedures and routines).  There are no separate 'F', 'p',
    -- or 'P' codes in this catalog; procedures and related
    -- routines are reported under 'f'.  The earlier `('r', 'S', 'f',
    -- 'F', 'p', 'P')` filter conflated the `defaclobjtype` code set
    -- with the different `acldefault()` code set (which uses
    -- lowercase 's' for sequences) and listed codes that do not
    -- exist.  See
    -- https://www.postgresql.org/docs/current/catalog-pg-default-acl.html
    -- and
    -- https://www.postgresql.org/docs/current/functions-info.html
    FOR bad_kind IN
        SELECT DISTINCT
            CASE d.defaclobjtype
                WHEN 'r' THEN 'TABLES'
                WHEN 'S' THEN 'SEQUENCES'
                WHEN 'f' THEN 'FUNCTIONS'
                ELSE d.defaclobjtype::text
            END
          FROM pg_catalog.pg_default_acl d
          JOIN pg_catalog.pg_roles r
            ON r.oid = d.defaclrole
          JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) AS acl ON true
         WHERE r.rolname = 'circus_owner'
           AND d.defaclobjtype IN ('r', 'S', 'f')
           AND acl.grantee = 0::oid
           AND acl.privilege_type IS NOT NULL
           AND acl.privilege_type <> ''
    LOOP
        RAISE EXCEPTION USING
            ERRCODE = 'PZ001',
            MESSAGE = 'migration_invariant: circus_owner retains a default grant to PUBLIC on ' || bad_kind;
    END LOOP;
END
$$;

-- 13. Record this migration.  ON CONFLICT keeps the migration
--     idempotent: a database that already has
--     000003_runtime_grant_hardening in its ledger does not produce a
--     duplicate row, and the runner treats it as a true no-op.
INSERT INTO circus.circus_schema_migrations(version)
VALUES ('000003_runtime_grant_hardening')
ON CONFLICT (version) DO NOTHING;

COMMIT;
