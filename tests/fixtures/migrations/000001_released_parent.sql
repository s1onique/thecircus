-- 000001_released_parent
--
-- Released-parent upgrade fixture.  Represents the database state after
-- the released 000001_event_journal and 000002_namespace_alignment have
-- both been applied.  Both versions are recorded in the canonical
-- circus.circus_schema_migrations ledger; the circus.* tables and
-- triggers already exist; circus_app exists; but circus_owner does NOT
-- exist because the released 000001 only created the application role
-- and 000002 does not reconcile roles.
--
-- This fixture exists to prove that 000003_runtime_grant_hardening
-- can be applied by the production migration runner to a database
-- whose only authority role is the application role, and that 000003
-- creates circus_owner before it is referenced.  A previous ordering
-- defect had 000003 attempting to
--   CREATE SCHEMA circus_extensions AUTHORIZATION circus_owner;
-- before the role reconciliation block, which raised
--   ERROR: role "circus_owner" does not exist
-- on every released upgrade.
BEGIN;

-- 1.  Mirror the released 000001: create only the application role.
--     circus_owner is intentionally absent and will be reconciled by
--     000003 itself.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'circus_app') THEN
        CREATE ROLE circus_app LOGIN;
    END IF;
END
$$;

-- 2.  Create the canonical circus.* schema and the three application
--     tables as the released 000001 / 000002 would have left them.
CREATE SCHEMA IF NOT EXISTS circus;

CREATE TABLE IF NOT EXISTS circus.circus_event_journal (
    journal_position bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source text NOT NULL,
    event_id text NOT NULL,
    event_type text NOT NULL,
    subject text NOT NULL,
    observed_at timestamptz NOT NULL,
    instance_id text NOT NULL,
    epoch_id uuid NOT NULL,
    sequence bigint NOT NULL CHECK (sequence >= 0),
    run_id uuid NOT NULL,
    envelope_json jsonb NOT NULL,
    raw_body bytea NOT NULL,
    raw_body_sha256 bytea,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT circus_event_journal_source_event_id_uq UNIQUE (source, event_id),
    CONSTRAINT circus_event_journal_stream_sequence_uq UNIQUE (instance_id, epoch_id, sequence),
    CONSTRAINT circus_event_journal_raw_body_sha256_ck
        CHECK (raw_body_sha256 IS NULL OR octet_length(raw_body_sha256) = 32)
);

CREATE TABLE IF NOT EXISTS circus.circus_run_projection (
    run_id uuid PRIMARY KEY,
    state text NOT NULL,
    started_journal_position bigint NULL,
    finished_journal_position bigint NULL,
    repository_ref text NULL,
    act_id text NULL,
    leamas_version text NULL,
    git_revision text NULL,
    started_by text NULL,
    started_at timestamptz NULL,
    outcome text NULL,
    finished_at timestamptz NULL,
    duration_ms bigint NULL,
    summary text NULL,
    checks_passed integer NULL,
    checks_failed integer NULL,
    checks_skipped integer NULL,
    first_journal_position bigint NOT NULL,
    last_journal_position bigint NOT NULL,
    conflict_count integer NOT NULL DEFAULT 0,
    version bigint NOT NULL DEFAULT 1,
    CONSTRAINT circus_run_projection_version_ck CHECK (version >= 1),
    CONSTRAINT circus_run_projection_conflict_count_ck CHECK (conflict_count >= 0)
);

CREATE TABLE IF NOT EXISTS circus.circus_schema_migrations (
    version text PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- 3.  The released 000001 installed the append-only trigger function
--     and triggers; recreate them so 000003's DROP TRIGGER has work
--     to do and so 000003 can re-author the canonical trigger state.
CREATE OR REPLACE FUNCTION circus.prevent_journal_modification()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'journal is append-only';
END;
$$;

CREATE TRIGGER circus_event_journal_prevent_update
    BEFORE UPDATE ON circus.circus_event_journal
    FOR EACH ROW EXECUTE FUNCTION circus.prevent_journal_modification();
CREATE TRIGGER circus_event_journal_prevent_delete
    BEFORE DELETE ON circus.circus_event_journal
    FOR EACH ROW EXECUTE FUNCTION circus.prevent_journal_modification();

-- 4.  Mark every circus.* object as owned by the migration authority
--     (postgres).  The released 000001 / 000002 do not transfer
--     ownership to a dedicated role because no dedicated role
--     existed; circus_owner is reconciled by 000003.
ALTER TABLE circus.circus_event_journal OWNER TO postgres;
ALTER TABLE circus.circus_run_projection OWNER TO postgres;
ALTER TABLE circus.circus_schema_migrations OWNER TO postgres;
ALTER FUNCTION circus.prevent_journal_modification() OWNER TO postgres;

-- 5.  Record 000001 and 000002 as already applied in the canonical
--     ledger.  000003 is what this fixture requires the runner to
--     apply; its presence in the ledger after the run is the proof
--     that the released-upgrade path is no longer broken.
INSERT INTO circus.circus_schema_migrations(version, applied_at)
VALUES ('000001_event_journal', '2026-01-01 00:00:01+00')
ON CONFLICT (version) DO NOTHING;
INSERT INTO circus.circus_schema_migrations(version, applied_at)
VALUES ('000002_namespace_alignment', '2026-01-01 00:00:02+00')
ON CONFLICT (version) DO NOTHING;

COMMIT;
