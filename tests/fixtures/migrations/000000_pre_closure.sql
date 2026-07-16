-- 000000_pre_closure
--
-- Pre-closure Circus ingestion fixture.  Represents the database state
-- the parent ACT (ACT-CIRCUS-INGESTION-JOURNAL01) left behind: tables
-- created in the `public` schema, a public migration ledger recording
-- 000001_event_journal as already applied, raw_body bytea with no raw
-- digest, and minimal role/grants that did not yet enforce the
-- authoritative circus.* namespace or runtime least privilege.
--
-- The fixture is checked in so the migration suite can apply the
-- production migration runner against an actual legacy state and prove
-- that 000002_namespace_alignment independently reconciles every
-- pre-closure object into the circus namespace without relying on a
-- re-run of 000001.
BEGIN;

-- 1.  The parent ACT scaffold created only the application role.
--     The released 000001 created circus_app and never created
--     circus_owner; 000002 does not reconcile roles either.  Mirror
--     the released state so the corrective migration must build
--     circus_owner from scratch before referencing it.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'circus_app') THEN
        CREATE ROLE circus_app LOGIN;
    END IF;
END
$$;

-- 2.  The parent ACT scaffold created its three application tables in
--     the public schema with no raw_body_sha256 column.
CREATE TABLE IF NOT EXISTS public.circus_event_journal (
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
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT circus_event_journal_source_event_id_uq UNIQUE (source, event_id),
    CONSTRAINT circus_event_journal_stream_sequence_uq UNIQUE (instance_id, epoch_id, sequence)
);

CREATE TABLE IF NOT EXISTS public.circus_run_projection (
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

CREATE TABLE IF NOT EXISTS public.circus_schema_migrations (
    version text PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- 3.  Seed a representative journal row and projection row that the
--     corrective migration must preserve byte-for-byte and semantically.
INSERT INTO public.circus_event_journal
    (source, event_id, event_type, subject, observed_at,
     instance_id, epoch_id, sequence, run_id, envelope_json, raw_body)
VALUES
    ('urn:test:legacy', 'legacy-event-1', 'io.leamas.execution.started.v1',
     'run/legacy-1', now(),
     'legacy-instance', '00000000-0000-0000-0000-000000000001',
     1, '00000000-0000-0000-0000-0000000000a1',
     '{"id":"legacy-event-1","type":"io.leamas.execution.started.v1"}'::jsonb,
     decode('5b226964225d3a5b226c65676163792d6576656e742d31225d7d', 'hex'));

INSERT INTO public.circus_event_journal
    (source, event_id, event_type, subject, observed_at,
     instance_id, epoch_id, sequence, run_id, envelope_json, raw_body)
VALUES
    ('urn:test:legacy', 'legacy-event-2', 'io.leamas.execution.finished.v1',
     'run/legacy-1', now(),
     'legacy-instance', '00000000-0000-0000-0000-000000000001',
     2, '00000000-0000-0000-0000-0000000000a1',
     '{"id":"legacy-event-2","type":"io.leamas.execution.finished.v1"}'::jsonb,
     decode('5b226964225d3a5b226c65676163792d6576656e742d32225d7d', 'hex'));

INSERT INTO public.circus_run_projection
    (run_id, state, started_journal_position, finished_journal_position,
     repository_ref, leamas_version, started_at,
     outcome, finished_at, duration_ms, summary,
     checks_passed, checks_failed, checks_skipped,
     first_journal_position, last_journal_position, conflict_count, version)
VALUES
    ('00000000-0000-0000-0000-0000000000a1', 'Completed', 1, 2,
     'legacy-repo', '0.9.0', now(),
     'succeeded', now(), 1000, 'legacy summary',
     3, 0, 0,
     1, 2, 0, 2);

-- 4.  Record 000001 as already applied in the public ledger.  This is
--     the exact pre-closure state the closure ACT must reconcile.
INSERT INTO public.circus_schema_migrations(version)
VALUES ('000001_event_journal')
ON CONFLICT (version) DO NOTHING;

-- 5.  Mark the legacy owner of the public tables so the corrective
--     migration must restate circus_owner ownership.
ALTER TABLE public.circus_event_journal OWNER TO postgres;
ALTER TABLE public.circus_run_projection OWNER TO postgres;
ALTER TABLE public.circus_schema_migrations OWNER TO postgres;

COMMIT;