# #4577 candidate replay harness

Replays managed-key exchange authority against a pinned honua-server image:

1. `ADMIN_PASSWORD=<strong password> ./boot.sh` boots an isolated Production stack
   (`IMAGE=` overrides the pinned digest). It uses its own network, publishes no host ports,
   and puts Caddy TLS on `candidate.honua.test`.
2. Copy Caddy's root certificate out to `root.crt`. Then apply `compile-fn.sql` (taken from the
   candidate's `tests/seed/client-compat-v1.sql`), `fixture.sql` and `fixture-role.sql`, and
   restart the server.
3. Run the scripts in a `python:3.12-alpine` container on the stack network, with this directory
   mounted at `/work` (put `admin.pw` there):
   - `replay.py` checks the generateToken bridge.
   - `replay_client_credentials.py` checks the client_credentials grant. It needs
     `Authentication__PortalToken__OAuth2__EnableClientCredentials=true`.
   - `replay_role_label.py` checks whether a permission label is projected as a role.

The receipts and logs here come from `nightly-aot-7ba4226`. They record statuses, error codes,
feature names and body hashes only.
