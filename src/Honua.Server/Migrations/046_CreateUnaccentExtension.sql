-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Enable the PostgreSQL `unaccent` extension.
--
-- OGC API Features advertises the CQL2 accent-insensitive-comparison
-- conformance class (cql2/accent-insensitive-comparison), and the Postgres CQL2
-- translator emits `UNACCENT(LOWER(...))` for the ACCENTI() function. Without the
-- extension that call resolves to a missing function (SQLSTATE 42883) and the
-- request fails with HTTP 500. `unaccent` ships with postgresql-contrib (present
-- in the postgis/postgis images), so installing it makes the advertised
-- conformance class actually work. IF NOT EXISTS keeps re-runs safe.
CREATE EXTENSION IF NOT EXISTS unaccent;
