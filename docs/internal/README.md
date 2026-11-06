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
- [Historical evidence index](https://github.com/honua-io/honua-server/blob/213e7ef3bf243fddf30c06a4cdb4cac737b9bfc8/docs/internal/evidence/README.md) — compatibility, conformance, parity, and migration evidence

Write run evidence, reports and receipts beneath the ignored `artifacts/` directory. CI uploads run output as workflow artifacts; release evidence belongs on the corresponding GitHub Release in `honua-release`.
