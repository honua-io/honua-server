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

## Read-only runtime and writable paths

Run as UID/GID `1001:1001`, with a read-only root filesystem and a writable
`/tmp` tmpfs (`mode=1777`, `nosuid`, `noexec`). Set mount ownership to `1001:1001`
for persistent directories. The image has no shell or package manager. Its
exec-form health check uses the compiled probe; ECS can use the load balancer's
`/healthz/ready` directly.

| Writes | Default location | Configuration or mount |
|---|---|---|
| Import/export staging, native reader scratch, GP spills, materialized operation certificates, ASP.NET request buffering | `/tmp` and `honua-*` children | `TMPDIR`; mount a writable tmpfs there |
| Temporary-file service | `/tmp/honua-temp` | `TemporaryFiles__StorageDirectory` |
| Local file storage | `/tmp/honua-storage`; Compose uses `/var/lib/honua/storage` | `FileStorage__LocalStorage__BasePath`; persistent volume for retained files |
| GP output and import staging | `/tmp/honua-geoprocessing-outputs`, `/tmp/honua-import-staging` | `Geoprocessing__Executors__OutputRootDirectory`, `Geoprocessing__Executors__ImportStagingDirectory` |
| Native/user caches | `/tmp/honua-cache` | `XDG_CACHE_HOME` |
| ASP.NET data protection without the durable Redis channel | `/tmp/.aspnet/DataProtection-Keys` | `HOME=/tmp`; mount the key directory persistently and share it across replicas |
| Durable operation-channel data protection | Redis | Existing Redis connection and `Operations__SecretChannel__KeyRingCertificatePath`; mount the certificate read-only |
| Logs | stdout/stderr | Container log driver; `/tmp/honua-logs` is reserved for an explicitly configured file sink |
| Optional .NET diagnostic sockets | disabled by default; under `TMPDIR` if enabled | `DOTNET_EnableDiagnostics`; explicitly mounted `DOTNET_DiagnosticPorts` if used |

Do not retain keys or durable file storage only in tmpfs. For multiple replicas,
use the configured shared stores. Overriding `TMPDIR` does not change explicit
paths in configuration; set the corresponding options too.

The `chiseled-extra` base retains certificates, ICU and time-zone data. The image
adds DejaVu Sans and the GSSAPI dependency closure. Only AOT stages libatomic.
The image smoke exercises legend label glyphs, German ICU number formatting,
Honolulu's UTC offset and New York's winter/summer offsets in the built JIT
runtime. The same smoke runs import/export and rendering as a non-root process
with a read-only root and writable `/tmp`.

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
