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
migrations do not need backfilled claims; hash immutability remains a separate gate.
