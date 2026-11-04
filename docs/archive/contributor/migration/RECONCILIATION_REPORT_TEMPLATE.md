# Reconciliation Report Template

Structured reporting format for the reconciliation harness output (Epic F, Phase 3).
Aligns with the four harness checks from the
[migration plan](../ESRI_MIGRATION_PLATFORM_PLAN.md#reconciliation-harness).

This is a reporting template, not a harness implementation. The template defines the
expected output structure that the harness should produce.

---

## Report Metadata

| Field | Value |
|-------|-------|
| Import run ID | |
| Source service | |
| Target Honua service | |
| Timestamp | |
| Server version | |

## Feature Count Comparison

| Layer | Source Count | PostGIS Count | Delta | Status |
|-------|-------------|---------------|-------|--------|
| | | | | Pass / Fail |

## Geometry Validity

| Metric | Value |
|--------|-------|
| Total geometries checked | |
| Valid (ST_IsValid) | |
| Invalid | |
| **Status** | Pass / Fail |

## Key Attribute Sampling

Configured field list for attribute verification.

| Field | Null Count (Source) | Null Count (PostGIS) | Type Match | Cardinality Match | Status |
|-------|---------------------|----------------------|------------|-------------------|--------|
| | | | Yes / No | Yes / No | Pass / Fail |

## Spatial Extent Comparison

| Metric | Source | PostGIS | Delta | Tolerance | Status |
|--------|--------|---------|-------|-----------|--------|
| xmin | | | | | Pass / Fail |
| ymin | | | | | Pass / Fail |
| xmax | | | | | Pass / Fail |
| ymax | | | | | Pass / Fail |
| SRID | | | | N/A | Match / Mismatch |

## Aggregate Status

| Field | Value |
|-------|-------|
| **Overall status** | Pass / Fail |
| Critical mismatches | |
| Non-critical observations | |

## Follow-Up Actions

| Action | Priority | Linked Ticket |
|--------|----------|---------------|
| | | |
