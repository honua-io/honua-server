# ADR-0080: Adopt the Fair Core License (FCL-1.0-ALv2)

## Status
Accepted (2026-10-23)

## Context
Honua Server and Honua Collect were published under the Elastic License 2.0 (ADR-0024). ELv2 has no
conversion clause, bars every third-party managed deployment including integrators installing Honua
into a customer's own environment, and carries another vendor's name. The commercial model
([editions and licensing](../../../concepts/editions-and-licensing.md)) is free Community
self-hosting plus signed-license Pro and Enterprise entitlements, which needs a
license that protects license-key functionality and the future hosted offering while remaining
honest with the open-source community about what it is.

## Decision
Relicense Honua Server and Honua Collect under the Fair Core License, Version 1.0, ALv2 Future
License (FCL-1.0-ALv2), a Fair Source license drafted by Keygen on Sentry's Functional Source
License. Licensor: Honua Geo Inc.

- Permitted Purpose: any use other than a Competing Use, expressly including internal use,
  non-commercial education and research, and professional services delivered to a licensee.
- Limitations: no circumventing or removing license-key functionality. `NOTICE` states that this
  includes the signed license envelope and edition and entitlement gating.
- Future license: each version is additionally licensed under Apache-2.0 two years after it is made
  available.
- Prior versions remain under ELv2. SDKs, Console, Studio and the open protocol standards stay
  Apache-2.0.

## Consequences
- NuGet packages use `PackageLicenseFile` (FCL has no SPDX identifier); container images carry
  `LicenseRef-FCL-1.0-ALv2`. GitHub reports the license as "Other", as it did for ELv2.
- Integrators deploying Honua into a customer environment need no agreement; hosting Honua as their
  own managed service is a Competing Use and requires a partner agreement.
- The repository does not accept external contributions (`close-external-prs.yml`), so Honua Geo
  Inc. retains full copyright and the ability to relicense.
- Journaled migrations keep their original header bytes and immutable ledger hashes. Historical exact-byte classifier review records are retained in `eng/schema/reviewed-migration-hashes.v1.json`. New migrations use the Fair Core License header and must be idempotent and tested on an installed stack. There is no prior-reader CI gate.
- The attachment preamble (`src/Honua.Server/MigrationContracts/attachment-schema-preamble.v1.sql.template`)
  keeps its immutable v1 bytes. That preamble is pinned by SHA-256 in `ServerCoreSchemaMigrations`,
  and a header change there makes every non-default-schema attachment migration refuse to load.
