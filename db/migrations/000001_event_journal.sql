-- Migration: 000001_event_journal
-- Creates the append-only event journal and run projection tables.
-- Uses raw SQL for migration to avoid adding a migration framework dependency.

BEGIN;

-- =============================================================================
-- Schema
-- =============================================================================

CREATE SCHEMA IF NOT EXISTS circus;

-- =============================================================================
-- Event Journal Table
-- =============================================================================

CREATE TABLE circus_event_journal (
    journal_position bigint
        GENERATED ALWAYS AS IDENTITY
        PRIMARY KEY,

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

    -- Uniqueness constraints
    CONSTRAINT circus_event_journal_source_event_id_uq
        UNIQUE (source, event_id),

    CONSTRAINT circus_event_journal_stream_sequence_uq
        UNIQUE (instance_id, epoch_id, sequence)
);

-- =============================================================================
-- Indexes
-- =============================================================================

CREATE INDEX circus_event_journal_run_id_position_idx
    ON circus_event_journal (run_id, journal_position);

CREATE INDEX circus_event_journal_event_type_position_idx
    ON circus_event_journal (event_type, journal_position);

CREATE INDEX circus_event_journal_received_at_position_idx
    ON circus_event_journal (received_at, journal_position);

-- =============================================================================
-- Append-Only Enforcement: Trigger to Reject Updates and Deletes
-- =============================================================================

CREATE OR REPLACE FUNCTION circus.prevent_journal_modification()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'Journal modification is not allowed. The event journal is append-only.';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER circus_event_journal_prevent_update
    BEFORE UPDATE ON circus_event_journal
    FOR EACH ROW
    EXECUTE FUNCTION circus.prevent_journal_modification();

CREATE TRIGGER circus_event_journal_prevent_delete
    BEFORE DELETE ON circus_event_journal
    FOR EACH ROW
    EXECUTE FUNCTION circus.prevent_journal_modification();

-- =============================================================================
-- Application Role
-- =============================================================================

-- Create an application role with restricted permissions
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'circus_app') THEN
        CREATE ROLE circus_app LOGIN;
    END IF;
END
$$;

-- Grant only SELECT and INSERT on journal table
-- Explicitly deny UPDATE, DELETE, TRUNCATE
GRANT USAGE ON SCHEMA circus TO circus_app;
GRANT SELECT, INSERT ON circus_event_journal TO circus_app;
GRANT USAGE, SELECT ON SEQUENCE circus_event_journal_journal_position_seq TO circus_app;

-- =============================================================================
-- Run Projection Table
-- =============================================================================

CREATE TABLE circus_run_projection (
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

    CHECK (conflict_count >= 0),
    CHECK (version >= 1)
);

-- Grant full access to projection (mutable table)
GRANT SELECT, INSERT, UPDATE, DELETE ON circus_run_projection TO circus_app;

-- =============================================================================
-- Migration Tracking
-- =============================================================================

CREATE TABLE IF NOT EXISTS circus_schema_migrations (
    version text PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

INSERT INTO circus_schema_migrations (version)
VALUES ('000001_event_journal')
ON CONFLICT (version) DO NOTHING;

COMMIT;

-- =============================================================================
-- Rollback (for testing purposes only - not for production use)
-- =============================================================================

-- DROP TRIGGER IF EXISTS circus_event_journal_prevent_update ON circus_event_journal;
-- DROP TRIGGER IF EXISTS circus_event_journal_prevent_delete ON circus_event_journal;
-- DROP FUNCTION IF EXISTS circus.prevent_journal_modification();
-- DROP TABLE IF EXISTS circus_run_projection;
-- DROP TABLE IF EXISTS circus_event_journal;
-- DROP TABLE IF EXISTS circus_schema_migrations;
-- DROP SCHEMA IF EXISTS circus;
-- DROP ROLE IF EXISTS circus_app;
