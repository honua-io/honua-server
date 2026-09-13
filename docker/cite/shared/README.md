# CITE database bootstrap

The WFS 1.0, 1.1 and 2.0 compositions boot the selected server image against
PostGIS before seeding. Honua runs its embedded migrations and checks their
physical schema effects before readiness succeeds. The `seed` service then
checks `public.schema_versions` against the selected source's server migration
files and inserts fixture data. TeamEngine depends on successful seed completion.
The suite runner starts only the test container after these dependencies pass.

`HONUA_CITE_SERVER_IMAGE` selects the image. For an externally built candidate,
`HONUA_CITE_MIGRATIONS_DIR` must point to that candidate's
`src/Honua.Server/Migrations` directory; the CITE workflows check out the supplied
source SHA for this purpose. Default-schema migration 109 adoption is omitted,
matching `PostgresDatabaseMigrationRunner`. No schema guard or migration safety
policy is disabled. The legacy catalog fixture disables the default GP sample
seed and catalog caching so the data becomes visible without a stale snapshot.

The former WFS bootstrap set `HONUA_SKIP_MIGRATIONS=true` and mounted a partial,
manually maintained schema under `/docker-entrypoint-initdb.d`. This omitted the
migration journal, metadata-v2 floor 031, outbox operation-instance columns and
alert audit outbox. The production runner was never called; this was not a
PostgreSQL 15 migration failure. The init SQL is now data-only and runs after
normal migration completion. Existing obsolete fixture volumes must be removed
with the fixture's normal `docker compose down --volumes` cleanup; they are not
silently adopted or repaired.

The OGC API Features stack already uses normal migration boot before its seed.
At the 9f2f16a image it reaches Ready with 132 journaled scripts. Client-compat's
related failure instead created migration-owned tables before the runner's
pre-upgrade consistency check (`SchemaExistsWithoutJournal`); that ordering is
tracked by #4735 / PR #4750.

Run the live bootstrap regression with a locally built server image:

```bash
HONUA_CITE_SERVER_IMAGE=honua-server:latest \
  bash docker/cite/shared/tests/test-wfs-bootstrap.sh
```

It creates isolated databases for all three WFS compositions, checks readiness
and the seeded catalog, then removes the metadata-floor journal entry and
requires immediate rejection with the missing migration name and no seed writes.
The same test runs in the evidence workflow and reusable WFS conformance jobs.

## WCS raster extension ordering

The WCS seed previously installed `postgis_raster` and created raster tables
*after* the first migration boot. Migration 055 had been journaled as a no-op
while raster support was absent; the hand-created columns then lacked its
required EXTERNAL storage. Restart failed with
`055_SetRasterDataExternalStorage.sql (JournalClaimsMissingSchema)`.

WCS now provisions only the PostGIS raster extension through initdb, before
Honua discovers its provider migration root. The data-only seed runs against
that migrated schema. The live `test-wcs-bootstrap.sh` regression verifies
provider migration receipts, the seeded rasters, EXTERNAL storage and successful
readiness/GetCapabilities after restart. It uses the same image environment
variable as the WFS regression and also runs in CITE workflows.
