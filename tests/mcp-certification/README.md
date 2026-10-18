# Server-owned MCP request cancellation harness

This harness owns the Honua-specific installed-client cancellation scenario and
independent PostGIS observer. The canonical client producer in `geospatial-mcp`
supplies the installed SDK clients and its authored-fixture recovery oracle. It
consumes these files from the exact attested candidate source using protected Git
history and verified immutable blobs. Missing files or source/hash mismatches
remain nonpass; there is no copied helper fallback in the specification repository.

`request-cancellation.mjs` captures the SDK's actual integer wire ID and session,
keeps the original request open, and sends public `notifications/cancelled`.
The observer locks the independently bound `test_service` layer 0 relation,
requires the original query to be blocked by that lock, and checks wrong-session
and string-versus-integer ID isolation for at least 600 ms each. The server polls
shared cancellation state every 200 ms. Rightful cancellation must return one
typed `-32800` response while a separate database connection proves the blocked
backend stopped and released its relation locks before blocker rollback.
Unchanged authored rows, successful independent recovery readback and all owned
cleanup are mandatory. A local AbortSignal rejection, process termination, or
notification acknowledgement cannot satisfy this proof. Durable GP cancellation
is a separate lifecycle.

The observer accepts only an explicitly configured owned loopback database and
the exact per-run server application name. Physical binding is restricted to the
maintained `honua.features`/`public.features` fixture shape. It does not discover
another dataset when the authored binding fails. Proofs retain hashes, typed IDs,
bounded counters and provider PIDs, never credentials or raw session tokens.
Database errors expose only SQLSTATE/fixed diagnostic codes, never raw stderr.
The blocker expires after 30 s; observer commands have a 3 s process budget, provider
discovery an 8 s polling budget, and response/cessation each a 5 s polling budget.

The existing focused MCP workflow runs these final owning paths on relevant PRs
using Node 22.19. It installs the official SDK 1.32.1 through the immutable reviewed
`geospatial-mcp` dependency lock at `372d9ad1366a5fa28165e7ebbc7481e6e1ca13c4`;
the specification repository owns the canonical-client pin, not this harness.
For local controls, install that lock with `npm ci --ignore-scripts` in its
`certification` directory, link its `node_modules` into this directory, then run
from this directory:

```sh
node --test test/*.test.mjs
```

The wire tests use the actual installed SDK against a deliberately scripted peer;
their provider observations are synthetic and never candidate evidence. The
separate integration control uses a disposable owned PostGIS database. Supply
`HONUA_MCP_OBSERVER_PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD`, and `PGSERVER_APP_NAME`
suffixes under `HONUA_MCP_OBSERVER_`, using a role allowed to create/drop that
disposable database, then run:

```sh
node --test test/postgres-observer.integration.mjs
```

That control proves real relation blocking, SQL cancellation, lock release,
unchanged rows, corruption refusal and a public-table decoy beside the authored
`honua` fixture. It does not prove MCP-to-server cancellation. Actual qualification
requires the canonical client run against the exact attested Honua candidate and
its retained complete cessation proof. Source and harness controls never replace
that receipt.
