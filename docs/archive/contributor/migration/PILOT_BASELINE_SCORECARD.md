# Pilot Baseline Scorecard

Captures the starting state at pilot kickoff. All fields align with the migration plan
[success metrics](../ESRI_MIGRATION_PLATFORM_PLAN.md#success-metrics-defined) and
[pilot dependency rule](../ESRI_MIGRATION_PLATFORM_PLAN.md#pilot-dependency-rule).

---

## Pilot Metadata

| Field | Value |
|-------|-------|
| Pilot name | |
| Tier | |
| Kickoff date | |
| Target end date | |
| Sponsor | |
| Technical owner | |
| Migration phase gate | |

## Source System Inventory

| Field | Value |
|-------|-------|
| Platform | |
| Service count | |
| Service types | |
| Layer count | |
| Feature count (estimate) | |
| CRS inventory | |
| Auth model | |
| Data source classification | |

## Target Client Inventory

| Client Application | Protocol Dependencies | Integration Points |
|--------------------|-----------------------|--------------------|
| | | |

## Baseline Metrics

Aligned with the four success metrics defined in the migration plan.

### Manual Rewrite Ratio (Denominator)

| Metric | Value |
|--------|-------|
| Total ArcGIS API call sites (scanner) | |
| Call-site breakdown by category | |

### Capability Coverage

| Metric | Value |
|--------|-------|
| Pilot-usage constructs inventoried | |
| Tier A count | |
| Tier B count | |
| Tier C count | |

### Time-to-First-Value

Not measured at baseline. Target: first migrated map + successful query within one focused implementation day.

### Data Correctness

| Metric | Value |
|--------|-------|
| Source feature counts (per layer) | |
| Geometry validity summary | |
| Spatial extents | |
| Attribute sampling baseline | |

## Pilot Dependency Rule Checklist

- [ ] Validated data path into PostGIS
- [ ] Automated reconciliation checks available
- [ ] Import pipeline observability in place (Epic J)

## Parity Scope

### In Scope

| Protocol | Operations |
|----------|------------|
| | |

### Explicitly Out of Scope

| Item | Reason |
|------|--------|
| | |

## Risk Register Snapshot

| Risk | Likelihood | Impact | Mitigation | Linked Blockers |
|------|------------|--------|------------|-----------------|
| | | | | |
