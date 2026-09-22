-- Provisional migration file: assign the next sequence only at source-apply time.
-- DEFAULT remains the base feature store (NULL VersionContext / NULL branch parent).
-- This durable descriptor is intentionally separate from the mutable branch registry.
-- Serialize concurrent first application as well as idempotent reruns in the
-- migration transaction. No response-time or manager-time identity allocation.
SELECT pg_advisory_xact_lock(hashtext('honua.gdb_version_store_identity.v1'));

CREATE TABLE IF NOT EXISTS honua.gdb_version_store_identity (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    version_id uuid NOT NULL UNIQUE DEFAULT gen_random_uuid()
        CHECK (version_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    created_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO honua.gdb_version_store_identity (singleton)
VALUES (true)
ON CONFLICT (singleton) DO NOTHING;

COMMENT ON TABLE honua.gdb_version_store_identity IS
    'Durable identity of the managed DEFAULT feature store; never a mutable gdb_versions row.';
