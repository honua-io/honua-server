---
type: guide
title: "How to choose the default or full serving image"
description: "Choose the image that includes your database stores while retaining file-format imports."
---
# How to choose the default or full serving image

The rc.2 default is the **chiseled JIT image with PostGIS as its only database
store**. Native AOT remains an explicit `-aot` variant; promotion requires full
suite and live-journey parity evidence by the 18 November evidence review. Choose the JIT **full** image when you need additional
database stores. File-format support is a separate concern: the default keeps
the import/export readers, including GeoPackage, Shapefile, GeoTIFF and Parquet.
Importing a GeoPackage does not require a SQLite database-store provider.

**Availability:** the image split is awaiting image-build evidence and
publication. Do not add `-full` to an older tag and assume that tag exists.
Select only an image listed in the release's published artifacts, verify its
revision, and pin its digest. Current general-purpose images are not evidence
that the new PostGIS-only boundary is installed.

## Choose a variant

| Your data sources | Choice after the split is published |
|---|---|
| PostGIS tables and supported file imports | Default serving image |
| DuckDB, SQL Server, MySQL, Oracle, Redshift, Snowflake, Databricks, or REST read-through | Matching JIT full image |
| A store absent from the installed inventory | Stop and select a compatible published image before configuring it |

The full image includes the additional stores; it does not promote experimental
providers to supported production status or enable their feature gates. Oracle
and Snowflake retain their JIT runtime requirement. Check each provider's
[data-source reference](../../reference/configuration/data-sources/README.md).

## Deploy and check

1. Inventory the primary store, optional store configuration, warehouse gates,
   and Connections already saved in this installation. Include stores you
   intend to keep after the upgrade.
2. Select the same release revision in the required variant. The planned
   nightly full tag is `nightly-jit-<candidate-sha7>-full`; release full tags
   use the `-full` suffix. Confirm actual publication before use and
   retain the immutable digest for deployment and rollback.
3. Back up the installation and follow your normal
   [Compose](docker-compose.md), [Kubernetes](kubernetes.md), or
   [upgrade procedure](upgrade-and-rollback.md). Changing image variant does
   not replace migration and rollback checks.
4. After startup, inspect the authenticated capabilities manifest's data-store
   inventory through your installed Honua client, where your release documents
   one. An absent inventory means unknown
   availability; it does not mean that every store is supported. A registration
   also does not prove connectivity or permission to use a particular database.
5. Check readiness and open the expected Connection as the intended operator.
   Test a representative read before restoring normal traffic.

The new chiseled serving runtime has no shell or package manager. For deployment
health probes, use direct execution of `/app/honua-healthcheck --ready` for
readiness; omit `--ready` for liveness. Update the probe and compatible image
together. Older images lack this executable and must retain their existing
probe until upgraded. Pull-only Compose installations inherit the pinned
image's health check.

## Writable paths with a read-only root

The chiseled serving images run as UID/GID `1001:1001`. Their default writable
root is `/tmp/honua`; mount it as a writable volume owned by that user, or as
`--tmpfs /tmp/honua:rw,noexec,nosuid,size=512m,uid=1001,gid=1001,mode=750`.
The image pre-creates its writable directories. A mounted empty root must permit
UID 1001 to create child directories. Capacity depends on concurrent import and
geoprocessing workloads; 512 MiB is the smoke-test allocation.

| Writes | Image default | Configuration |
| --- | --- | --- |
| Import staging, format extraction, spooling, materialized operation certificates, request buffering and other temporary files | `/tmp/honua` and `honua-*` children | `TMPDIR` (used by .NET `Path.GetTempPath()`) |
| Temporary-file service | `/tmp/honua/temp` | `TemporaryFiles__StorageDirectory` |
| Local file storage | `/tmp/honua/storage` | `FileStorage__LocalStorage__BasePath` |
| Geoprocessing output | `/tmp/honua/honua-geoprocessing-outputs` | `Geoprocessing__Executors__OutputRootDirectory`; defaults under `TMPDIR` |
| Geoprocessing import staging | `/tmp/honua/honua-import-staging` | `Geoprocessing__Executors__ImportStagingDirectory`; defaults under `TMPDIR` |
| Native/user caches | `/tmp/honua/cache` | `XDG_CACHE_HOME` |
| User home and fallback data-protection keys | `/tmp/honua/.aspnet/DataProtection-Keys` | `HOME`; Redis-backed deployments use the shared Redis key repository |
| Logs | Standard output/error | An operator-added file sink must use a separately configured writable path |

When moving the root, set `HOME`, `TMPDIR`, `XDG_CACHE_HOME`,
`TemporaryFiles__StorageDirectory` and `FileStorage__LocalStorage__BasePath`
together. Explicit geoprocessing overrides must point at writable paths too.
Local file storage and fallback data-protection keys need durable storage across
restarts; a tmpfs is suitable only for disposable data. The versioned Compose
configuration overrides Local storage to its persistent `/var/lib/honua/storage`
volume. Diagnostics are disabled by default; enabling .NET diagnostic sockets
requires a writable `TMPDIR` or an explicit writable diagnostic socket path.

The image health check executes `/app/honua-healthcheck --ready` directly and
requires no shell. ECS can use its load balancer's `/healthz/ready` probe.
`scripts/ci/smoke-serving-formats.sh` starts the server with a read-only root,
mounts only `/tmp/honua`, and imports fixture files through the upload API.

The `chiseled-extra` base retains certificates, ICU and time-zone data. The image
adds DejaVu Sans, the GSSAPI dependency closure and libatomic for Parquet.
The smoke exercises legend label glyphs, German ICU number formatting,
Honolulu's UTC offset and New York's winter/summer offsets in the built JIT runtime.

## Troubleshooting

- **Startup names an excluded store and the full variant:** use the matching
  full image. This can be caused by configured stores or retained Connections;
  deleting configuration alone does not remove a saved Connection.
- **You want to return to the default image:** first use the full image to remove
  unused additional-store Connections through normal administration. Review
  configuration and dependencies before switching back.
- **The image cannot be pulled:** check that the exact variant was published for
  that revision and platform. Do not silently substitute a moving channel tag.
- **A shell-based health probe fails:** pair the new image with its executable
  probe; do not disable readiness to make the deployment look healthy.

See [capability deployment profiles](capability-deployment-profiles.md) for
feature availability beyond the database-store inventory.
