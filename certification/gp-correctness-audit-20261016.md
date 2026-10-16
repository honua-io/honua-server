# GP correctness source audit

Compared with server trunk `e6271fd35ffb7b77779438fd853fe14cc067130c`, the only
changed matrix source is `ExternalPostgisSinkExecutor.cs`. Idempotency suffixes
now use accepted input row ordinals across every batch, advancing even when
`ON CONFLICT` inserts no rows. No operation registration, catalog contract,
destructive classification, or existing semantic-evidence verdict changes.

`ExecuteAsync_IdempotentBatches_PreserveAllRowsAcrossReplayWithDifferentBatchSize`
adds real PostGIS coverage: three valid rows, an omitted null geometry, batch
size one followed by replay with batch size two, and exact persisted row keys.
This regression requires CI execution; refreshing the source digest does not
claim live provider qualification or change any existing operation verdict.
