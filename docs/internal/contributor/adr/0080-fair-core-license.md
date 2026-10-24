# ADR-0080: Adopt the Fair Core License (FCL-1.0-ALv2)

## Status
Accepted (2026-10-23)

## Context
Honua Server and Honua Collect were published under the Elastic License 2.0 (ADR-0024). ELv2 has no
conversion clause, bars every third-party managed deployment including integrators installing Honua
into a customer's own environment, and carries another vendor's name. The commercial model
([editions and licensing](../../../concepts/editions-and-licensing.md)) is free Community
self-hosting plus signed-license Pro and Enterprise entitlements and capacity metering, which needs a
license that protects license-key functionality and the future hosted offering while remaining
honest with the open-source community about what it is.

## Decision
Relicense Honua Server and Honua Collect under the Fair Core License, Version 1.0, ALv2 Future
License (FCL-1.0-ALv2), a Fair Source license drafted by Keygen on Sentry's Functional Source
License. Licensor: Honua Geo Inc.

- Permitted Purpose: any use other than a Competing Use, expressly including internal use,
  non-commercial education and research, and professional services delivered to a licensee.
- Limitations: no circumventing or removing license-key functionality. `NOTICE` states that this
  includes the signed license envelope, edition and entitlement gating, and capacity registration,
  metering and enforcement.
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
- Journaled SQL migrations recorded in the frozen schema reader baseline
  (`SchemaReaderBaseline.json`, plus 129) keep their original ELv2 header bytes: the
  migration-safety classifier accepts them by exact SHA-256, and a header rewrite would
  either break startup or require re-blessing hashes that record a historical review.
  Those files are Honua-authored and covered by the repository LICENSE regardless of
  the comment text; new migrations use the FCL header.
- Capacity metering must stay on the license-validated code path to remain covered by the
  Limitations clause.
