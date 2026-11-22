-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- honua:reader-incompatible reason=canonical App content kinds and dependencies replace kinds required by prior readers
-- Historical versions and events are immutable. The store reads their original names privately;
-- new writes use app. Restore a pre-upgrade backup and matching server to roll back.
ALTER TABLE $HonuaSchema$.content_publication_versions
    DROP CONSTRAINT IF EXISTS content_publication_versions_kind;
ALTER TABLE $HonuaSchema$.content_publication_versions
    ADD CONSTRAINT content_publication_versions_kind
    CHECK (kind IN ('map', 'dashboard', 'report', 'app', 'generated-app'));

ALTER TABLE $HonuaSchema$.content_publication_routes
    DROP CONSTRAINT IF EXISTS content_publication_routes_kind;
ALTER TABLE $HonuaSchema$.content_publication_routes
    ADD CONSTRAINT content_publication_routes_kind
    CHECK (kind IN ('map', 'dashboard', 'report', 'app', 'generated-app'));
