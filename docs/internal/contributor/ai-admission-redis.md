# Redis authority for AI invocation admission

The Redis backend uses one non-expiring hash per stable deployment identity. Policy, generation,
stop epoch, charged attempts and successful control audit events share that hash. Configure durable
Redis persistence and no eviction; this is an authority rather than a cache. AI admission requires
observed `appendonly=yes`, `appendfsync=always`, `no-appendfsync-on-rewrite=no`,
`maxmemory-policy=noeviction`, `aof_enabled=1` and `aof_last_write_status=ok`. The weaker `everysec`
job-substrate attestation is insufficient for this backend. The runtime Redis principal must be
able to read the relevant INFO and CONFIG GET observations. Missing permissions or unobservable
settings deny AI admission; deterministic paths do not inspect or initialize this authority.

Every store call observes the actual ledger key owner, its process/replication identity and strict
settings before and after the script. Redirects are disabled. The script also compares the
observed process/replication identity and live AOF health on the Redis process actually executing
it. A changed route or unhealthy/unobservable authority makes the outcome unavailable, even if a
conservative charge may already have committed. Observation is bounded by a three-second backend
timeout, shortened to one second for permit checks and always linked to the caller's deadline.
These checks qualify an operator-managed durable primary; they do not establish automatic HA or
rollback safety. Process identity is not permanently bound in the ledger, so an ordinary Redis
restart retaining the always-fsynced ledger can preserve authority. Potentially history-losing
failover requires quiescence and the recovery procedure below.

Ordinary startup, reconnect and missing policy never initialize the ledger or select a memory
fallback. A missing or corrupt authority denies model calls and invalidates live permits.

Authenticated initialization creates a fresh stopped generation and a 3,630-second quarantine.
This applies to first setup and to recovery after complete key loss. A separate authorized resume
is required after quarantine. Prior generation permits and approvals cannot control the new ledger.
An application/coordinator restart retains the same authoritative ledger. This does not establish
Redis high-availability, crash or restore qualification. Restoring older RDB/AOF data, including an
unacknowledged persistence gap, can roll back charges and stop epochs while retaining the same
generation. Privileged same-authority rollback and individual field deletion cannot reliably be
detected. Never infer complete history from a surviving key alone.

Before rollback or restore, quiesce AI invocations across every replica. Preserve incident evidence
and retire the old authority without deleting its hash. Explicitly configure a new deployment
identity consistently on all replicas, initialize its fresh stopped generation, wait the full
3,630-second accounting/deadline/drain quarantine using a trustworthy Redis server clock, then
separately authorize resume. Do not reuse old authority approvals. This conservative recovery
procedure covers uncertain history; it does not claim transparent automatic failover safety. Never
clear the ledger to recover capacity.

One atomic script uses Redis server time to check deployment, canonical actor, actor/use-case and
optional verified workflow limits together. A workflow limit aggregates across actors. One granted
attempt charges every applicable scope; a denial charges none. Release is idempotent and fenced by
generation, attempt, epoch and fixed deadline; it does not refund the attempt. Stop invalidates
permits across replicas. The invocation deadline is at most 3,600 seconds, with a fixed 30-second
reservation drain allowance. Heartbeats do not extend either deadline.

The ledger retains at most 8,192 attempts. An attempt is pruned only after the maximum supported
3,600-second rolling window and after its reservation is released or expired. This preserves
history when an operator enlarges a policy window. A short current window does not reduce that
history requirement; operators must qualify the resulting retained-record capacity for their
workload. Unexpired accounting is never evicted to admit another call.

Successful controls atomically retain the canonical operator identity, invocation and correlation,
action, disposition and resulting policy generation/revision/epoch. Exact invocation replay returns
the original result; changing any sealed command field refuses replay. Expected generation, revision
and epoch still fence old commands after their audit event has aged out. Rejected and no-op controls
consume no successful-control capacity; the canonical operation gateway records their audit outcome.

Control history defaults to 30 days and at most 1,024 successful events. These are implementation
operating defaults, not asserted framework retention requirements. The final slot is reserved for
stop: other mutations refuse once 1,023 unexpired events exist, and stop can safely consume the last
slot. An already stopped ledger remains stopped and an additional stop is a no-op. At saturation,
keep the ledger intact and stopped; inspect/export its audit evidence and wait until the oldest
retained events meet the 30-day pruning horizon before an authorized policy change or resume.
There is no silent overwrite or delete-ledger recovery. Operator qualification must verify these
retention and capacity choices fit the deployment's audit and control frequency requirements.

Control metadata additionally has UTF-8 byte bounds (operator identity 4,096; invocation and
correlation 128 each), and every encoded ledger record is bounded to 16 KiB. Oversized or malformed
records refuse before mutation; Unicode identifiers within these bounds remain supported.

Redis persistence and replication limits: [persistence](https://redis.io/docs/latest/management/persistence/)
and [replication](https://redis.io/docs/latest/manual/replication/). These are substrate limitations,
not installed deployment qualification evidence.
