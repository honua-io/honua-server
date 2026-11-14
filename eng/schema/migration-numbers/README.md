# Migration number claims

For each new numbered SQL migration, add a claim at
`eng/schema/migration-numbers/<SQL directory>/<number>.txt`, using the same
zero-padded number (at least three digits). The file contains the repository-relative
SQL path and a final newline. For example, migration
`src/Honua.Server/Migrations/170_Example.sql` claims
`eng/schema/migration-numbers/src/Honua.Server/Migrations/170.txt`.

Repository hygiene checks additions against freshly fetched trunk and reports the
next free number. Claims are immutable. They make two concurrent PRs with the same
number conflict in Git even if both checks passed before either PR landed. Renumber
the pending migration, its claim, and its hash pin after the first PR merges.
The Server and Postgres SQL directories have separate number sequences. Existing
migrations do not need backfilled claims. The same admission guard rejects edits,
deletions and renames of existing hash pins against trusted trunk, including paired
SQL-and-pin edits. Pins added only on newer trunk do not force stale branches to
refresh.

## Atomic policy cutover

This change removes the old shared hash manifest while installing the number guard.
Existing migration PRs that modify that manifest must resolve its deletion before
merging and adopt individual pins and number claims. An audit on 14 November 2026
found one open migration PR without a manifest edit: #7692 adds
`168_AddAgentAuditActor.sql`. Its number is reserved here so a newly admitted PR
cannot reuse it while that older PR retains its successful checks. The reservation
is immutable like every other claim; it does not certify the migration's hash.
