# 2026.1 capacity evidence producer

The normative capacity envelope is owned by
[honua-release](https://github.com/honua-io/honua-release/blob/trunk/docs/CAPACITY-ENVELOPE-2026.1.md).
This page records server-side evidence production; it does not amend its numbers.

`capacity-soak-candidate.yml` emits one attested ZIP containing a version-2
receipt and `honua.capacity-observations/v1`. The workflow source must be the
manifest-pinned candidate, and the candidate must already contain this producer.
The [operator runbook](ops/capacity-soak-receipt.md) describes dispatch, actual
measurement definitions, failure handling and verification.

The release promise is a post-freeze, full-envelope, single-tenant local-Docker
soak with all eight GA dimensions exercised and all eight SLOs recomputed from
attested raw observations. Multi-tenancy, alerting and offline sync retain their
Preview posture. Historical aggregate-only receipts cannot satisfy that promise.

Qualification is still outstanding. The candidate pinned when #5314 was
implemented (`87966c3f7b6c840ffc4d4da0b451714ab717b18a`) predates the collector;
it cannot attest code introduced in this PR. A new candidate containing the
producer must be built, pinned and soaked at its exact source commit. The
collector retains observed queue depths even when admission prevents filling the
100-job envelope. An incomplete or failing observation never becomes a passing
release receipt.
