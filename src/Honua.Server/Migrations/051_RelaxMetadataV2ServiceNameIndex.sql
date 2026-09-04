-- honua-server#1395: the Metadata v2 service-name lookup index was created UNIQUE on
-- (environment, revision, lower(name)). That contradicts the canonical graph model:
-- a single logical service is projected across multiple protocol facets (feature/map/
-- image/ogc/stac) that intentionally share one display name but carry distinct service
-- ids. The compat snapshot (honua.seed_metadata_v2_compat_snapshot / the read-time V1
-- bridge) emits exactly these same-named facets, so any publish that persisted such a
-- graph hit a raw Postgres 23505 on idx_metadata_v2_services_name. Service identity is
-- the PRIMARY KEY (environment, revision, service_id); the name column is only a lookup
-- aid, so the index must not be unique. Recreate it as a plain (non-unique) index.

DROP INDEX IF EXISTS honua.idx_metadata_v2_services_name;

CREATE INDEX IF NOT EXISTS idx_metadata_v2_services_name
    ON honua.metadata_v2_services_idx (environment, revision, lower(name));
