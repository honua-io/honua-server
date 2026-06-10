# ADR-0005: DbUp for Database Migrations

## Status
Accepted

## Context
Need a database migration strategy that:
- Works with Native AOT
- Tracks schema versions
- Runs embedded SQL scripts
- Minimal dependencies

Options considered:
- EF Core Migrations
- FluentMigrator
- DbUp
- Raw SQL scripts with manual tracking

## Decision
Use DbUp for database migrations.

**Why DbUp:**
- Lightweight (~10KB)
- Embeds SQL files in assembly
- Tracks versions in `SchemaVersions` table
- AOT compatible (no reflection)
- Simple API

## Consequences

### Positive
- SQL files are explicit and reviewable
- No reflection or code generation
- Works with any SQL (PostGIS functions, etc.)
- Easy to understand migration history

### Negative
- No automatic rollback generation
- Must write forward migrations manually
- No diff-based migration generation

### Mitigation
- Maintain `xxx_rollback.sql` files for emergencies
- Prefer additive changes (new columns, tables)
- Use blue-green deployments for major changes
