-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Persists the canonical content-hash input of each immutable Studio version (honua-server#5449).
--
-- content_hash is the SHA-256 of the server's serialization of the version envelope. The
-- envelope column is jsonb, which reorders object keys and normalises whitespace, so those bytes
-- cannot be re-derived from the stored envelope. Storing them lets the version read return them
-- (contentHashInput, base64) and a client recompute the digest independently.
--
-- Expand-only: the column is nullable. Versions saved before this migration (or by an older
-- binary during a rolling update) keep NULL; the reader rebuilds their input only when the
-- re-serialized envelope still hashes to the stored digest, and otherwise omits it.
--
-- Sequence numbering per ADR-0045: `125` is the current highest prefix as of this migration.
ALTER TABLE $HonuaSchema$.studio_content_versions
    ADD COLUMN IF NOT EXISTS content_hash_input BYTEA;
