# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Canonical client-certification fixture expectations.

One authority for what ``tests/seed/client-compat-v1.sql`` contains, so the
canonical-client lanes (GeoPandas, OWSLib, DuckDB Spatial, R sf/ows4R,
pystac-client) assert the same numbers instead of each re-deriving them.
Mirrors the machine-readable manifest at
``docs/gis/data/client-certification-fixture.v1.json``
([#3393](https://github.com/honua-io/honua-server/issues/3393)); the
``CanonicalFixtureManifestTests`` drift test keeps the two in lockstep.
"""

from __future__ import annotations

from pathlib import Path

TESTS_ROOT = Path(__file__).resolve().parents[2]
PROJECT_ROOT = Path(__file__).resolve().parents[3]

SEED_PATH = TESTS_ROOT / "seed" / "client-compat-v1.sql"
SERVER_CONFIG_PATH = TESTS_ROOT / "config" / "client-compat-server-v1.json"
FIXTURE_MANIFEST_PATH = (
    PROJECT_ROOT / "docs" / "gis" / "data" / "client-certification-fixture.v1.json"
)

SERVICE_ID = "test_service"
COLLECTION_ID = "0"

# Row counts in the seeded `features` table for layer 0.
TOTAL_FEATURES = 10
FEATURES_WITH_GEOMETRY = 9
ACTIVE_FEATURES = 5
INACTIVE_FEATURES = 5

# The first seeded feature ("alpha"), used as the geometry-fidelity anchor.
ANCHOR_NAME = "alpha"
ANCHOR_LON = -122.4900
ANCHOR_LAT = 37.7100

# Full seeded extent (all nine geometries), EPSG:4326.
FIXTURE_BBOX = (-122.4900, 37.7100, -122.3700, 37.7900)

# A bbox that selects a strict, stable subset (the first three points).
SUBSET_BBOX = (-122.4950, 37.7050, -122.4550, 37.7350)
SUBSET_BBOX_FEATURE_COUNT = 3

STORAGE_CRS_EPSG = 4326
PROJECTED_CRS_EPSG = 3857

# Attribute schema exposed to feature clients. `objectid` is the feature id
# and several clients surface it outside the attribute schema, so it is
# tracked separately rather than being required in every field listing.
FEATURE_ID_FIELD = "objectid"
ATTRIBUTE_FIELDS = (
    "name",
    "description",
    "status",
    "count",
    "ratio",
    "active",
    "created_at",
    "event_date",
    "event_time",
    "uid",
    "tags",
    "numbers",
)

# Stable equality filter: status = 'active' selects ACTIVE_FEATURES rows.
FILTER_FIELD = "status"
FILTER_VALUE = "active"

PAGE_SIZE = 3

# Control-plane surface used to substantiate CERT-AUTH-01/02 from lanes whose
# data protocol is anonymous in the client-compat fixture.
# Honua's control plane authenticates with an API key, not HTTP Basic: the
# bootstrap key is the value of HONUA_ADMIN_PASSWORD, presented in the
# X-API-Key header (src/Honua.Hosting/Features/Authentication/
# ApiKeyAuthenticationHandler.cs). The compose stack sets that env var to
# ADMIN_API_KEY below. `admin` / ADMIN_API_KEY is also the named-user
# credential pair the Portal/Sharing facade accepts on
# /sharing/rest/generateToken, which is what the arcgis-stub lane uses.
ADMIN_PROBE_PATH = "/api/v1/admin/services"
ADMIN_API_KEY_HEADER = "X-API-Key"
ADMIN_API_KEY = "ClientCompatAdmin123!"
ADMIN_USERNAME = "admin"
ADMIN_PASSWORD = ADMIN_API_KEY

# Deliberately invalid inputs for the error-handling facets.
UNKNOWN_COLLECTION_ID = "does-not-exist-9999"
MALFORMED_CQL2_FILTER = "status =="
