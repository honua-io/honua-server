---
type: guide
title: "Initialize the database without starting Honua"
description: "Run the guarded database migrations as a separate task before starting or updating a serving deployment."
---
# Initialize the database without starting Honua

An agent setting up a deployment that skips startup migrations must initialize
the database before reporting the deployment ready. Use the generic Honua server
image with `HONUA_MIGRATE_ONLY=true`. Use the same immutable release image that
will serve the database; the Lambda runtime image is not this task's entry point.

The task needs network access to the intended database and a connection with
migration permissions. PostgreSQL and PostGIS must already be provisioned. For
AWS, run inside the approved VPC and use the applied infrastructure's database
secret reference and network configuration. Do not expose a private database to
make initialization reachable. The canonical PostGIS preflight creates a missing
PostGIS extension when the migration role has permission; otherwise provision it
through the approved infrastructure bootstrap before retrying.

Pass the connection through `ConnectionStrings__DefaultConnection`, preferably
as an `aws:secretsmanager:`, `azure:keyvault:`, or `env:` reference supported by
Honua's existing secret resolver. Credentials do not belong in the prompt or Git.
Set `HONUA_MIGRATION_RUN_ID` to a UUID to correlate the approved task and its
result. Omission generates an ID; the ID is a correlation value, not an approval
credential or an exactly-once execution guarantee.

This mode uses the same migration journal, advisory lock, compatibility check,
expand/contract safety policy, backup hook and physical schema guard as ordinary
startup. It does not start HTTP, Redis, background workers or a control plane.
The migrate-only command ignores `HONUA_SKIP_MIGRATIONS=true`; it still applies
and verifies migrations. Execution-worker launch variables and non-Postgres
providers are rejected. Development and degraded-start settings cannot turn a
failed initialization into success.

## Result contract

The process emits exactly one JSON line on standard output using the canonical
`format: "honua.migrate-only/v1"` contract. Logs use standard error. There is no
receipt prefix. Exit status zero requires
`outcome: "completed"` and `schemaVerified: true`. The receipt includes:

- The run ID and UTC start/completion timestamps.
- `targetLocatorSha256`: a SHA-256 of the UTF-8 JSON array
  `["honua.database-locator/v1", host, port, database, metadataSchema]`.
  The host is trimmed, lowercased and stripped of a trailing dot; the port is
  an invariant decimal string. Database and schema case are preserved.
  Passwords, usernames and connection options are excluded. An explicit single
  host and database are required. This binds a configured locator; it does not
  attest physical database identity or equate a proxy with its backend.
- The metadata schema, `appliedScripts` array, and server informational version.
- `packagedMigrationManifestSha256`: a SHA-256 over sorted embedded SQL resource
  names and their content hashes, encoded as `name + newline + lowercase SHA-256
  + newline`. This identifies packaged migration inputs, not a database backup.

Failures and cancellations return the canonical nonzero typed exit status and
stable `code` described in [Run migrations out of band](upgrade-and-rollback.md#run-migrations-out-of-band), with no
success claim or raw provider exception text. A failed or interrupted task may
have committed migrations; reconcile against the same target using the canonical
runner. A repeat run against an already-current database verifies the schema and
reports zero applied scripts.

The deployment agent must additionally bind the receipt to the actual provider
task identity, approved image digest, secret reference and network scope. A JSON
receipt alone is not cloud execution evidence. Confirm serving readiness before
handoff. Keep [application rollback and database recovery](upgrade-and-rollback.md)
as separate decisions; running an older initializer does not roll back schema.
