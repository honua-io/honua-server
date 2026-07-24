-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Adds per-item ownership to studio_content_items (honua-server#3001), replacing the
-- created_by stand-in that GET /content-items' `owner` filter used prior to this migration
-- (see migration 089's comment and docs/internal/admin-api/studio-package-lifecycle.md).
-- studio_package_drafts already carries a real owner_id column (migration 035); this migration
-- brings studio_content_items to parity so ownership follows the content item, not just its
-- mutable drafts.
--
-- owner_id is populated once, at item creation, from the authenticated principal that created
-- the owning draft (which itself already defaults to the creating actor when not explicitly
-- assigned -- see StudioPackageLifecycleService.CreateDraftAsync) and is never overwritten by a
-- later draft/version upsert for the same item (PostgresStudioPackageStore.UpsertItemAsync's
-- ON CONFLICT clause intentionally excludes owner_id).
--
-- Existing rows predate the owner_id column, so they are backfilled from created_by: every
-- existing item's creator becomes its owner, matching the enumeration `owner` filter's prior
-- created_by-based behavior exactly (a zero-behavior-change backfill for already-migrated
-- deployments) so honua-server#3001's end-user authorization has a well-defined owner for
-- every pre-existing item from the moment the flag is turned on.
--
-- Sequence numbering per ADR-0045: `089` is the current highest prefix as of this migration.
ALTER TABLE honua.studio_content_items
    ADD COLUMN IF NOT EXISTS owner_id TEXT;

UPDATE honua.studio_content_items
SET owner_id = created_by
WHERE owner_id IS NULL
  AND created_by IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_studio_content_items_owner_list
    ON honua.studio_content_items (owner_id, updated_at DESC, item_id DESC)
    WHERE owner_id IS NOT NULL;
