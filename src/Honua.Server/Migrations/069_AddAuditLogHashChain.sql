-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Tamper-evident hash chain for the append-only audit trail (#350).
--
-- Migration 033 made honua.audit_log append-only (UPDATE / DELETE blocked by
-- rule). That protects against accidental application-level rewrites, but a
-- privileged actor who can DROP RULE could still rewrite, reorder, or delete
-- history without leaving a trace. This migration adds a cryptographic hash
-- chain so any such tampering is *detectable* after the fact:
--
--   entry_hash = SHA-256( prev_hash || canonical(row) )
--
-- where canonical(row) is a stable, ordered serialization of the row's
-- immutable fields and prev_hash is the entry_hash of the immediately
-- preceding row (by audit_id). The chain links every row to its predecessor,
-- so deletion or reordering breaks the chain at the affected point and
-- in-place mutation breaks the entry_hash of the mutated row.
--
-- The chain is computed by the application (PostgresAuditLog) under a
-- transaction-scoped advisory lock so concurrent inserts produce a consistent
-- linear chain. Older rows written before this migration have NULL hashes; the
-- verifier treats the first hashed row as the genesis of the chain.

ALTER TABLE honua.audit_log
    ADD COLUMN IF NOT EXISTS prev_hash  CHAR(64),
    ADD COLUMN IF NOT EXISTS entry_hash CHAR(64);

COMMENT ON COLUMN honua.audit_log.prev_hash IS
    'Hex SHA-256 entry_hash of the preceding audit row (by audit_id), or NULL for the genesis row / pre-069 rows. Part of the tamper-evident chain (#350).';
COMMENT ON COLUMN honua.audit_log.entry_hash IS
    'Hex SHA-256 of prev_hash concatenated with the canonical serialization of this row''s immutable fields. Recomputed by the integrity verifier to detect tampering (#350).';

-- The verifier walks the chain in audit_id order; the primary key already
-- provides that ordering, so no extra index is required.
