# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

# Canonical client-certification fixture expectations (R mirror).
#
# This is the R translation of `tests/python/shared/canonical_fixture.py`. The R
# `sf`/`ows4R` lane cannot import the Python module, so the constants are
# mirrored here verbatim. Do NOT invent lane-local numbers: if the fixture
# changes, `tests/python/shared/canonical_fixture.py` and
# `docs/gis/data/client-certification-fixture.v1.json` change first and this
# file follows.

# Repository-relative anchors. `TESTS_ROOT` resolves from this file's location so
# the same script works from a checkout and from the container bind mount
# (/workspace/tests/r/certification/...).
# `cert_script_dir()` is defined in cert_envelope.R; source that file first.
cf_tests_root <- function() {
  normalizePath(file.path(cert_script_dir(), "..", ".."), mustWork = TRUE)
}

cf_project_root <- function() {
  # tests/ -> repository root. Inside the lane container only `tests/` is
  # mounted, so this may not exist; callers must tolerate that.
  normalizePath(file.path(cf_tests_root(), ".."), mustWork = FALSE)
}

cf_seed_path <- function() file.path(cf_tests_root(), "seed", "client-compat-v1.sql")
cf_server_config_path <- function() {
  file.path(cf_tests_root(), "config", "client-compat-server-v1.json")
}

SERVICE_ID <- "test_service"
COLLECTION_ID <- "0"

# Seeded layer identity. `LAYER_NAME` is the `honua.layers.layer_name` value in
# tests/seed/client-compat-v1.sql; the WFS 2.0 feature type name the server
# derives from it is `honua:test_layer`, which is how this lane recognises the
# certification target among the other seeded services.
LAYER_NAME <- "Test Layer"
WFS_TYPE_LOCAL_NAME <- "test_layer"

# Row counts in the seeded `features` table for layer 0.
TOTAL_FEATURES <- 10L
FEATURES_WITH_GEOMETRY <- 9L
ACTIVE_FEATURES <- 5L
INACTIVE_FEATURES <- 5L

# The first seeded feature ("alpha"), used as the geometry-fidelity anchor.
ANCHOR_NAME <- "alpha"
ANCHOR_LON <- -122.4900
ANCHOR_LAT <- 37.7100

# Full seeded extent (all nine geometries), EPSG:4326.
FIXTURE_BBOX <- c(xmin = -122.4900, ymin = 37.7100, xmax = -122.3700, ymax = 37.7900)

# A bbox that selects a strict, stable subset (the first three points).
SUBSET_BBOX <- c(xmin = -122.4950, ymin = 37.7050, xmax = -122.4550, ymax = 37.7350)
SUBSET_BBOX_FEATURE_COUNT <- 3L

STORAGE_CRS_EPSG <- 4326L
PROJECTED_CRS_EPSG <- 3857L

# Attribute schema exposed to feature clients. `objectid` is the feature id and
# several clients surface it outside the attribute schema, so it is tracked
# separately rather than being required in every field listing.
FEATURE_ID_FIELD <- "objectid"
ATTRIBUTE_FIELDS <- c(
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
  "numbers"
)

# Stable equality filter: status = 'active' selects ACTIVE_FEATURES rows.
FILTER_FIELD <- "status"
FILTER_VALUE <- "active"

PAGE_SIZE <- 3L

# Control-plane surface used to substantiate CERT-AUTH-01/02 from lanes whose
# data protocol is anonymous in the client-compat fixture.
ADMIN_PROBE_PATH <- "/api/v1/admin/services"
ADMIN_USERNAME <- "admin"
ADMIN_PASSWORD <- "ClientCompatAdmin123!"

# Honua's control plane authenticates with an API key header, not HTTP Basic and
# not a bearer login flow: src/Honua.Hosting/Features/Authentication/
# ApiKeyAuthenticationHandler.cs (`ApiKeyHeader = "X-API-Key"`). A 401 carries
# `WWW-Authenticate: ApiKey realm="Honua Admin", header="X-API-Key"`.
ADMIN_API_KEY_HEADER <- "X-API-Key"
ADMIN_API_KEY <- ADMIN_PASSWORD
ADMIN_AUTH_CHALLENGE_SCHEME <- "ApiKey"

# Deliberately invalid inputs for the error-handling facets.
UNKNOWN_COLLECTION_ID <- "does-not-exist-9999"
MALFORMED_CQL2_FILTER <- "status =="
