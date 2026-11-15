# PostgreSQL AI admission authority

PostgreSQL is the sole authority for AI invocation admission. Hosting registers the admission
coordinator; the database provider registers its scoped admission store directly. Missing or
unavailable storage denies invocation. There is no memory authority or runtime backend fallback.

Apply migrations 140 and 142 before using the financial policy. Keep the deployment identity and
database stable across replicas and upgrades. Startup never initializes or resumes a ledger.
Initialize an explicit policy through the canonical admin admission operations, inspect its state,
and authorize resume through the installed operation policy. A replacement deployment identity is
a different budget authority and must not be used to reset accounting.

The former `AiExecutionAdmission:Backend` selector is retired. Remove it from configuration.
Legacy `auto` and `postgres` settings remain accepted; an explicit `redis` or invalid setting
prevents startup. Migrating a Redis-backed deployment requires an explicit operator plan for its
outstanding invocations and charged history; starting an empty PostgreSQL ledger does not transfer
those obligations or establish a safe budget reset.

PostgreSQL admission tests cover atomic limits across independent connections, lease expiry,
stop and generation fences, idempotent controls, and financial send and settlement accounting.
Coordinator tests cover ambiguous acquisition, cancellation and unavailable authority. These source
tests do not establish installed recovery, mixed-version operation or remote cancellation bounds.

The focused AI Admission Contract workflow runs the PostgreSQL store tests alongside Reporting
financial admission and Core/Studio admission tests, sequentially in Release with warnings as errors.
A passing workflow is source validation; installed failover and recovery still require live proof.

See [AI invocation admission](../../reference/configuration/ai-execution-admission.md) for operator
controls, identity rules, supported financial policy and migration requirements.
