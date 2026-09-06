# Alert lifecycle domain audit atomicity

Issue #3865 protects the 2026.1 Alerting Preview promise that operator lifecycle
mutations have durable domain evidence. A request-level `admin.post` audit does
not substitute for `alert.acknowledge`, `alert.suppress`, or `alert.resolve`.

The hosted API uses one PostgreSQL transaction for the lifecycle upsert and the
hash-chained successful domain audit. The strict audit writer propagates failures;
the generic best-effort audit interface retains its existing behavior. A missing
audit identity, database failure, or connection loss aborts the whole transaction.
The chain lock is acquired before lifecycle writes, so concurrent hosts use the
same lock order and serialize retry admission.

Clients retain `X-Correlation-ID` (1–64 characters) when retrying an operation.
Actor, correlation, action and event identify that operation. Matching note and
suppression expiry return the existing receipt without another write. Reusing
that identity with different details returns 409. A new intentional operation
uses a new correlation ID. Suppression retries remain admissible after their
original expiry because durable-receipt lookup precedes new-operation validation.
Notes and suppression expiry are stored in domain audit details, and its timestamp
is the same instant as the lifecycle update. Migration 113 adds the retry index.

The real Postgres fixture seeds a uniquely identified ops event and invokes the
authenticated hosted Admin API. A test-only trigger observes the new lifecycle
row immediately before the audit INSERT, increments a nontransactional sequence,
then either raises an error or terminates its own database connection. The sequence
proves the exact fault boundary independently of rolled-back state. Each action
must return 500 with zero visible lifecycle rows and zero successful domain audits.
After complete host disposal/recreation, the same correlation succeeds once; an
identical retry preserves the timestamp and a changed-note retry returns 409.
SQL asserts actor, event, note, state, all nullable action columns, timestamps,
correlation, action and outcome, and the production audit-chain verifier checks
the resulting chain. These tests are implemented; execution results are pending.

Exact-candidate image/source identity and the retained raw HTTP/SQL/TRX restart
receipt remain candidate qualification obligations. No candidate qualification
is claimed by this source change or its local regression run.
