---
type: index
title: "Internal engineering docs"
description: "Contributor notes, ADRs, spikes and evidence; not part of the published product documentation."
---
# Internal engineering documentation

This section holds Honua's internal engineering material: contributor guides, architecture decision records (ADRs), CI/CD gate definitions, conformance and certification evidence, design drafts, and security remediation logs.

It is not part of the product documentation. If you are deploying, integrating with, or migrating to Honua, start at the [documentation home](../README.md).

Browse the internal tree on GitHub: [`docs/internal/`](https://github.com/honua-io/honua-server/tree/trunk/docs/internal).

Key entry points:

- [Contributor guide](contributor/README.md) — building, testing, and contributing to Honua Server
- [Architecture decision records](contributor/adr/README.md)
- [CI gate model](ci/gate-model.md)
- Operations: [protocol profiles](operations/protocol-profiles.md), [schema routing upgrade correction](operations/schema-routing-upgrade-correction.md), [AWS operations events](operations/aws-ops-events.md), [Launch Stack heartbeat](operations/aws-ops-heartbeat.md), [ECS deployment alarm admission](operations/aws-deployment-alarm-admission.md), [stop autonomous operations](operations/ops-global-kill-switch.md), [anonymous work during database pressure](operations/anonymous-pressure-admission.md), [deploy rollback recovery fence](admin-api/deploy-recovery-fence.md)
- Developer contracts: [AI Builder SDK contract](developer/ai-builder-sdk-contract.md), [AI Builder contract fixtures](developer/ai-builder-contract-fixtures.md), [package review API](developer/package-review-api.md)
- Security: [base URL resolution and open-redirect handling](security/base-url-and-open-redirect-handling.md), [code-scanning remediation](security/code-scanning-2026-Q2-remediation.md)
- [Feature map](features/README.md) and [native local-process transport](features/native-local-processes.md)
- Admin API notes: [analysis content](admin-api/analysis-content.md), [Console workflow packages](admin-api/console-workflow-packages.md), [Operate observability fixtures](admin-api/operate-observability-fixtures.md), [scene dataset registry](admin-api/scene-dataset-registry.md), [Studio package lifecycle](admin-api/studio-package-lifecycle.md), [back-office field workflows](developer/back-office-field-workflows-api.md), [SDK compatibility metadata](developer/SDK_COMPATIBILITY_METADATA.md)
- CI: [derived artifact normalization](ci/derived-artifact-normalization.md), [candidate GP canary evidence](ci/gp-canary-evidence.md), [PR Gate build cache](ci/pr-gate-build-cache.md), [source-generator inventory](contributor/source-generator-inventory.md)
- [Audit coverage matrix](operations/audit-coverage-matrix.md)
- [Geocode server evaluation matrix](spikes/geocode-server-matrix.md)
- More operations: [realtime streams](operations/realtime.md), [tile-cache batch worker contract](operations/tile-cache-batch-worker-contract.md), [AOT attestation verification](operations/aot-image-attestations-proof.md), [nightly images](../ops/nightly-images.md)
- Protocol coverage: [OGC API Features](protocol-coverage/ogc-api-features-coverage.md), [OGC API Tiles](protocol-coverage/ogc-api-tiles-coverage.md), [OGC API Records](protocol-coverage/ogc-api-records-coverage.md), [OGC API Coverages](protocol-coverage/ogc-api-coverages-coverage.md), [WCS 2.0.1](protocol-coverage/wcs-2.0.1-coverage.md)
- GIS protocol notes: [VectorTileServer routes](../gis/vector-tile-server.md), [ImageServer identify catalog contract](../gis/IMAGESERVER_IDENTIFY_REFERENCE.md)
- Pages that scripts generate or read: [client certification roster](../gis/CLIENT_CERTIFICATION_ROSTER.md), [portal facade seed contract](../gis/PORTAL_FACADE_SEED_CONTRACT.md), [temporal animation API](../gis/temporal-animation-api.md), [managed feature streams](../reference/gp-managed-feature-streams.md)
- [Historical evidence index](https://github.com/honua-io/honua-server/blob/213e7ef3bf243fddf30c06a4cdb4cac737b9bfc8/docs/internal/evidence/README.md) — compatibility, conformance, parity, and migration evidence

Write run evidence, reports and receipts beneath the ignored `artifacts/` directory. CI uploads run output as workflow artifacts; release evidence belongs on the corresponding GitHub Release in `honua-release`.
