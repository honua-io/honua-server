# Verified incident episode lifecycle

The incident episode store projects trusted observations into a durable lifecycle. It is
separate from the immutable observation journal: a finding identifies a condition, while
an episode identifies one occurrence of that condition in an authority scope.

Only the trusted evidence collector supplies signals. No public endpoint accepts a
caller-provided finding ID as proof that an incident exists. Collectors must derive
`AuthorityScope` from authenticated installation or tenant context and preserve a stable
`ObservationId` across retries. Recovery requires a complete, fresh evidence pass;
missing, truncated or failed collection is `Unknown`, never recovery.

An `Active` signal opens an episode with a generated ID and lifecycle version 1.
Repeated active observations refresh evidence without invalidating that version.
`Unknown` preserves the open episode and increments the version when its state changes.
`Recovered` closes it. A later activation gets a new episode ID. Out-of-order evidence
cannot reopen or refresh a closed episode. Observations with the same timestamp are
ordered by state, `Active` before `Unknown` before `Recovered`, so arrival order never
closes an incident; a later recovery closes it. Duplicate observation IDs preserve their
original result, including across tasks and process restarts.

The resolver accepts an authority scope, episode ID and lifecycle version. It returns
only the matching active, open episode with evidence from the last five minutes.
Unknown, recovered, stale, mismatched or missing episodes cannot authorize an action.
Historical reads remain available for recovery and recurrence narratives. The resolver
is an admission check; downstream mutations must retain their own concurrency and
approval checks when evidence changes after resolution.

PostgreSQL serializes each authority/finding transition using a row lock inside one
statement. It does not rely on session affinity or process-local locks. State and
idempotency records commit together. There is no automatic history pruning without a
separate retention policy.

This foundation alone does not collect evidence, send notifications or demonstrate a
live self-operation gate pass. Those require the collector, notification outbox and an
installed environment qualification.
