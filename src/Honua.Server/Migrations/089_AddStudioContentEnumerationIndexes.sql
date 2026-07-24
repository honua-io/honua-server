-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Supporting indexes for Studio content-item and package-draft enumeration (#3003).
-- GET /api/v1/studio/content-items and GET /api/v1/studio/package-drafts filter by
-- family, workspace, and owner (owner filters studio_content_items.created_by as a
-- stand-in until honua-server#3001 lands per-item ownership; studio_package_drafts
-- already has a real owner_id column) and page with a keyset cursor ordered by
-- (updated_at DESC, id DESC). Sequence numbering per ADR-0045: `088` is the current
-- highest prefix as of this migration, so this file uses `089` (no renumbering of the
-- pre-existing grandfathered collision groups).

-- Content item list: general keyset scan plus per-filter composites. `state` (draft /
-- current / published) is derived from current_version_id / published_version_id and
-- is not a stored column, so no dedicated index is needed for it beyond what these
-- composites already provide via the two pointer columns.
CREATE INDEX IF NOT EXISTS idx_studio_content_items_list
    ON honua.studio_content_items (updated_at DESC, item_id DESC);

CREATE INDEX IF NOT EXISTS idx_studio_content_items_family_list
    ON honua.studio_content_items (family, updated_at DESC, item_id DESC);

CREATE INDEX IF NOT EXISTS idx_studio_content_items_creator_list
    ON honua.studio_content_items (created_by, updated_at DESC, item_id DESC)
    WHERE created_by IS NOT NULL;

-- Package draft list: general keyset scan plus per-filter composites (existing
-- idx_studio_package_drafts_item / idx_studio_package_drafts_owner from migration 035
-- lack the draft_id tiebreak the keyset cursor needs, so they are left in place and
-- augmented here rather than altered).
CREATE INDEX IF NOT EXISTS idx_studio_package_drafts_list
    ON honua.studio_package_drafts (updated_at DESC, draft_id DESC);

CREATE INDEX IF NOT EXISTS idx_studio_package_drafts_family_list
    ON honua.studio_package_drafts (family, updated_at DESC, draft_id DESC);

CREATE INDEX IF NOT EXISTS idx_studio_package_drafts_owner_list
    ON honua.studio_package_drafts (owner_id, updated_at DESC, draft_id DESC)
    WHERE owner_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_studio_package_drafts_workspace_list
    ON honua.studio_package_drafts (workspace_id, updated_at DESC, draft_id DESC)
    WHERE workspace_id IS NOT NULL;

-- Content Publication Registry join support (REQ-004): batch-resolve the newest
-- publication-registry version referencing a Studio content item id (stored by
-- convention as content_publication_versions.source_content_id = itemId, see
-- PostgresContentPublicationStore.GetLatestRouteStatesBySourceContentIdsAsync), so the
-- content-items list endpoint can join in lifecycle badges without one query per row.
CREATE INDEX IF NOT EXISTS idx_content_publication_versions_source_content
    ON honua.content_publication_versions (source_content_id, created_at DESC, revision DESC)
    WHERE source_content_id IS NOT NULL;
