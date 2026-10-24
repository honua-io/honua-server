-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- GP process releases and bindings (honua-server#6221). A release is an immutable lock
-- (honua.gp-process-release-lock/v1) keyed by the digest of its canonical JSON; a binding is
-- the versioned, compare-and-set mapping from a logical process name to the release it
-- executes. Every activation appends to the history so a receipt can prove which release each
-- binding version named. Neither table is ever updated in place except the binding row's
-- version-guarded activation.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.gp_process_release (
    lock_digest text PRIMARY KEY,
    process_name text NOT NULL,
    process_version text NOT NULL,
    lock_document jsonb NOT NULL,
    published_by text NOT NULL,
    published_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT gp_process_release_digest_shape CHECK (lock_digest ~ '^sha256:[0-9a-f]{64}$'),
    CONSTRAINT gp_process_release_process_shape CHECK (process_name ~ '^[a-z][a-z0-9-]{1,62}$')
);

CREATE INDEX IF NOT EXISTS ix_gp_process_release_process
    ON $HonuaSchema$.gp_process_release (process_name, published_at DESC);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.gp_process_binding (
    process_name text PRIMARY KEY,
    binding_version integer NOT NULL,
    lock_digest text NOT NULL REFERENCES $HonuaSchema$.gp_process_release (lock_digest),
    previous_lock_digest text NULL REFERENCES $HonuaSchema$.gp_process_release (lock_digest),
    targets jsonb NOT NULL,
    operation text NOT NULL,
    activated_by text NOT NULL,
    activated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT gp_process_binding_version_positive CHECK (binding_version >= 1),
    CONSTRAINT gp_process_binding_operation CHECK (operation IN ('deploy', 'rollback'))
);

CREATE TABLE IF NOT EXISTS $HonuaSchema$.gp_process_binding_history (
    process_name text NOT NULL,
    binding_version integer NOT NULL,
    lock_digest text NOT NULL,
    previous_lock_digest text NULL,
    targets jsonb NOT NULL,
    operation text NOT NULL,
    activated_by text NOT NULL,
    activated_at timestamptz NOT NULL,
    PRIMARY KEY (process_name, binding_version)
);

COMMENT ON TABLE $HonuaSchema$.gp_process_release IS
    'Immutable GP process release locks keyed by canonical-JSON digest (honua-server#6221).';
COMMENT ON TABLE $HonuaSchema$.gp_process_binding IS
    'Live binding of a logical GP process name to its active release; binding_version is the compare-and-set guard for activation.';
COMMENT ON TABLE $HonuaSchema$.gp_process_binding_history IS
    'Append-only record of every binding activation, including rollbacks.';
