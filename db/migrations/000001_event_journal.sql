-- 000001_event_journal
-- Durable Circus ingestion schema.  The migration/owner role is distinct
-- from circus_app, which is granted only the supported ingestion operations.
BEGIN;

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

CREATE SCHEMA IF NOT EXISTS circus;
ALTER SCHEMA circus OWNER TO circus_owner;

-- The parent ACT scaffold created these three application tables in public.
-- Move that supported pre-closure state before CREATE TABLE IF NOT EXISTS so
-- already-applied environments retain their authority rather than acquiring a
-- duplicate fallback table.
DO $$
BEGIN
    IF to_regclass('public.circus_event_journal') IS NOT NULL
       AND to_regclass('circus.circus_event_journal') IS NULL THEN
        ALTER TABLE public.circus_event_journal SET SCHEMA circus;
    END IF;
    IF to_regclass('public.circus_run_projection') IS NOT NULL
       AND to_regclass('circus.circus_run_projection') IS NULL THEN
        ALTER TABLE public.circus_run_projection SET SCHEMA circus;
    END IF;
    IF to_regclass('public.circus_schema_migrations') IS NOT NULL
       AND to_regclass('circus.circus_schema_migrations') IS NULL THEN
        ALTER TABLE public.circus_schema_migrations SET SCHEMA circus;
    END IF;
    IF to_regclass('public.circus_event_journal_journal_position_seq') IS NOT NULL
       AND to_regclass('circus.circus_event_journal_journal_position_seq') IS NULL THEN
        ALTER SEQUENCE public.circus_event_journal_journal_position_seq SET SCHEMA circus;
    END IF;
END
$$;

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
    raw_body_sha256 bytea NOT NULL CHECK (octet_length(raw_body_sha256) = 32),
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT circus_event_journal_source_event_id_uq UNIQUE (source, event_id),
    CONSTRAINT circus_event_journal_stream_sequence_uq UNIQUE (instance_id, epoch_id, sequence)
);
ALTER TABLE circus.circus_event_journal
    ADD COLUMN IF NOT EXISTS raw_body_sha256 bytea;
ALTER TABLE circus.circus_event_journal OWNER TO circus_owner;

CREATE INDEX IF NOT EXISTS circus_event_journal_run_id_position_idx
    ON circus.circus_event_journal (run_id, journal_position);
CREATE INDEX IF NOT EXISTS circus_event_journal_event_type_position_idx
    ON circus.circus_event_journal (event_type, journal_position);
CREATE INDEX IF NOT EXISTS circus_event_journal_received_at_position_idx
    ON circus.circus_event_journal (received_at, journal_position);

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

DROP TRIGGER IF EXISTS circus_event_journal_prevent_update ON circus.circus_event_journal;
DROP TRIGGER IF EXISTS circus_event_journal_prevent_delete ON circus.circus_event_journal;
CREATE TRIGGER circus_event_journal_prevent_update
    BEFORE UPDATE ON circus.circus_event_journal
    FOR EACH ROW EXECUTE FUNCTION circus.prevent_journal_modification();
CREATE TRIGGER circus_event_journal_prevent_delete
    BEFORE DELETE ON circus.circus_event_journal
    FOR EACH ROW EXECUTE FUNCTION circus.prevent_journal_modification();

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
ALTER TABLE circus.circus_run_projection OWNER TO circus_owner;

CREATE TABLE IF NOT EXISTS circus.circus_schema_migrations (
    version text PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
ALTER TABLE circus.circus_schema_migrations OWNER TO circus_owner;

-- Explicitly remove inherited/default/public authority before the narrow
-- runtime grants.  UPDATE/DELETE/TRUNCATE on the journal are intentionally
-- absent and ownership remains with circus_owner.
REVOKE ALL PRIVILEGES ON SCHEMA circus FROM PUBLIC, circus_app;
REVOKE ALL PRIVILEGES ON TABLE circus.circus_event_journal FROM PUBLIC, circus_app;
REVOKE ALL PRIVILEGES ON TABLE circus.circus_run_projection FROM PUBLIC, circus_app;
REVOKE ALL PRIVILEGES ON TABLE circus.circus_schema_migrations FROM PUBLIC, circus_app;
REVOKE ALL PRIVILEGES ON SEQUENCE circus.circus_event_journal_journal_position_seq FROM PUBLIC, circus_app;
REVOKE ALL PRIVILEGES ON FUNCTION circus.prevent_journal_modification() FROM PUBLIC, circus_app;

GRANT USAGE ON SCHEMA circus TO circus_app;
GRANT SELECT, INSERT ON TABLE circus.circus_event_journal TO circus_app;
GRANT USAGE, SELECT ON SEQUENCE circus.circus_event_journal_journal_position_seq TO circus_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE circus.circus_run_projection TO circus_app;
GRANT EXECUTE ON FUNCTION circus.prevent_journal_modification() TO circus_app;

-- Future objects owned by the migration role do not become public by default.
ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner IN SCHEMA circus REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner IN SCHEMA circus REVOKE ALL ON SEQUENCES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner IN SCHEMA circus REVOKE ALL ON FUNCTIONS FROM PUBLIC;

INSERT INTO circus.circus_schema_migrations(version)
VALUES ('000001_event_journal')
ON CONFLICT (version) DO NOTHING;

COMMIT;
