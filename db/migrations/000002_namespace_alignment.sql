-- 000002_namespace_alignment
-- Reconcile the pre-closure scaffold, which created its application tables in
-- public, with the one supported circus namespace.  The public references in
-- this corrective migration are legacy detection only; all resulting objects
-- and all runtime names are circus-qualified.
BEGIN;

CREATE SCHEMA IF NOT EXISTS circus;

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

-- Environments created by the partial scaffold did not have the raw digest.
-- The raw bytes themselves are retained unchanged.  New rows are required to
-- provide the digest by the application insert command.
ALTER TABLE circus.circus_event_journal
    ADD COLUMN IF NOT EXISTS raw_body_sha256 bytea;

ALTER TABLE circus.circus_event_journal
    DROP CONSTRAINT IF EXISTS circus_event_journal_raw_body_sha256_ck;
ALTER TABLE circus.circus_event_journal
    ADD CONSTRAINT circus_event_journal_raw_body_sha256_ck
    CHECK (raw_body_sha256 IS NULL OR octet_length(raw_body_sha256) = 32);

INSERT INTO circus.circus_schema_migrations(version)
VALUES ('000002_namespace_alignment')
ON CONFLICT (version) DO NOTHING;

COMMIT;
