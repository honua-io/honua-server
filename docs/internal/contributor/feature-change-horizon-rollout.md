# Feature-change horizon rollout

The change-feed cursor is a committed prefix of the global feature generation sequence.
Migration 213 introduces `honua.feature_change_read_horizon()` as the common source of
that cursor for sync, replica registration, version ancestors and realtime streaming.
It initially returns the committed maximum. Both feature-writer triggers retain the
global transaction advisory lock, so a later generation cannot commit before an earlier
allocated generation. Sequence allocations that roll back are harmless holes.

This is the reader prerequisite for #7505 and #7393; it does not remove write contention.
The function is deliberately replaceable without changing readers in a running process.
Streaming bounds both event delivery and resume-window calculations by the horizon.
Readers that inspect changes for upload conflicts still inspect committed changes;
those probes do not advance a download cursor.

## Deployment order

1. Deploy this reader release with migration 213 and the existing writer lock.
2. Establish that every supported running reader, including the supported previous
   release during rolling deployment, calls the shared function for its cursor.
3. Introduce a committed-prefix publisher and replace the function body to read its
   published generation. Only then narrow the DEFAULT and branch writer locks together.

A raw `MAX(generation)` reader cannot coexist safely with writers that commit out of
allocation order. For example, writer A allocates generation 10 and remains open,
writer B commits generation 11, and the old reader records cursor 11. When A commits,
resuming strictly after 11 loses its change. Merely filtering by the oldest active
transaction ID also fails: transaction IDs can be allocated before generations.
The publisher must fence allocations as well as transaction completion.

## Required proof for writer changes

Hold one writer after generation allocation and prove a second layer's writer commits
before releasing the first. Record a real cursor between their commits, then commit
the first and resume without losing either change. Repeat with rollback, same-layer
writes, branch-overlay writes, and COPY/import alongside an edit. Use bounded
cancellation and database state to establish ordering. Compare blocking and commit
latency on the build host. Run the N-1 reader check with the actual supported baseline;
the presence of this migration alone does not prove that older binaries use it.
