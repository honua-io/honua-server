# ADR-0045: Defer Renumbering of Colliding Migration Sequence Numbers

## Status

Accepted

## Context

The May 2026 structural audit (`structural-audit-2026-05`) flagged Group D
"quick hygiene" item D2: eight collision groups in the
`src/Honua.Server/Migrations/` sequence prefix space. As of this ADR the
colliding files are:

| Prefix | Files                                                                                              |
|--------|----------------------------------------------------------------------------------------------------|
| 018    | `018_AddCloudRasterCatalog.sql`, `018_AddPerformanceIndexes.sql`                                   |
| 024    | `024_AddFieldCollectionSync.sql`, `024_CreateFeatureChangeOutbox.sql`                              |
| 025    | `025_AddMultidimensionalCoverageCatalog.sql`, `025_CreateOperationalDataSchema.sql`                |
| 029    | `029_CreateMigrationDataSourceCatalog.sql`, `029_CreateMigrationPerformanceEvidence.sql`           |
| 031    | `031_CreateMetadataV2Snapshot.sql`, `031_CreateMigrationRunCatalog.sql`                            |
| 033    | `033_CreateAuditLog.sql`, `033_CreateTileCacheCatalog.sql`                                         |
| 034    | `034_CreateMetadataV2ReleasePackages.sql`, `034_CreateOperateObservability.sql`                    |
| 035    | `035_CreateAnalysisContent.sql`, `035_CreateFormPackages.sql`, `035_CreateStudioPackageLifecycle.sql` |

These are seventeen distinct migrations that landed on independent branches
without coordinating sequence numbers. The DbUp runner
(`PostgresDatabaseMigrationRunner`) executes scripts in lexicographic order
discovered via `WithScriptsEmbeddedInAssembly`, so the collision does not
break execution: each file still has a unique filename, the journal still
records each by its full embedded-resource name, and the eventual ordering is
deterministic. What it does break is **human navigation** ("what was the last
migration applied?") and any future tooling that expects a single migration
per prefix.

## Decision

**Defer renumbering. Keep the collisions in place. Enforce uniqueness on new
migrations going forward.**

## Rationale

DbUp's default Postgres journal (`schema_versions`) tracks applied migrations
by **script name**, where the script name is the full embedded-resource path
(e.g. `Honua.Server.Migrations.018_AddCloudRasterCatalog.sql`). The journal
is not content-addressed.

Renaming any of the seventeen files would change its script name. DbUp would
then observe two effects against any database that has already applied the
original script:

1. The original script name becomes "executed but not discovered"
   (`GetExecutedButNotDiscoveredScripts`), which is non-fatal but pollutes
   the diagnostic surface and trips operator alerting.
2. The renamed script name is "not yet applied" and DbUp would attempt to
   re-execute it on the next startup.

A spot check of the colliding files shows the majority contain at least one
statement that is **not** idempotent under re-execution (`ALTER TABLE …
ADD CONSTRAINT`, `CREATE OR REPLACE FUNCTION` with body changes, `INSERT`
seed rows, `CREATE INDEX` without `IF NOT EXISTS`). Re-running these would
either fail loudly (constraint already exists), silently corrupt seed data
(duplicate inserts), or take an unbounded lock at startup (rebuilding a
large index). None of those outcomes are acceptable for a forward migration
that is only intended to fix a cosmetic ordering issue.

Safer alternatives we considered:

- **Content-hash journal:** DbUp does not ship a content-hash journal out of
  the box, and writing one would require a one-shot data migration to
  back-fill hashes for every row already in `schema_versions` across every
  deployed environment we do not control. Net negative.
- **Alias table:** Insert rows into `schema_versions` for the renamed names
  before DbUp runs, on every startup, as part of the renumbering migration
  itself. This works but is a permanent operational footgun: forget the alias
  step in any future environment restore and you re-run all seventeen
  scripts. The alias step also needs to know the **old** name, which means
  the rename is not actually a rename — both names live forever in code.
- **Rename only on greenfield:** Hold the rename until we have a clean signal
  that no production environment has these migrations applied. We do not have
  that signal and will not have it for the foreseeable future.

The risk/reward is clearly skewed against renumbering: the cost is "the
prefix list is harder to read," the benefit is "future migrations need
discipline to not collide further." We accept the cost and address the
benefit with the consequence below.

## Consequences

- The seventeen colliding files stay on disk under their current names.
  Lexicographic ordering inside each collision group is alphabetical by
  suffix, which is the order DbUp will execute them in.
- New migrations **must** use a prefix strictly greater than the current
  maximum (`037` at the time of writing). Reviewers should reject any PR
  that introduces a new collision. The existing
  `DatabaseMigrationSafetyTests` architecture test suite is the natural
  place to add a uniqueness check on future prefixes (excluding the
  grandfathered set above) if collisions become a recurring problem.
- When the metadata-v2 cutover branch and the subsequent modularization
  work consolidate the migration catalogue (e.g. extracting per-provider
  migrations into provider-specific assemblies, or introducing a
  content-hash journal as part of the modular-monolith Phase 1), the
  grandfathered prefixes can be revisited as part of that larger move.

## References

- `~/.claude/projects/-home-makani-honua-server/memory/structural-audit-2026-05.md`
  — Group D quick-hygiene items.
- `ADR-0005: DbUp Migrations`.
- `src/Honua.Postgres/Features/Infrastructure/Migrations/PostgresDatabaseMigrationRunner.cs`
  — journal configuration.
- `tests/dotnet/Honua.Architecture.Tests/DatabaseMigrationSafetyTests.cs`
  — existing migration safety checks.
