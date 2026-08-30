# HEAD-dependent probe inventory

Issue [#3486](https://github.com/honua-io/honua-server/issues/3486) audited compose files,
workflows, scripts, and their deployment-guide examples for `wget --spider` and `curl -I`.
Those options issue HEAD requests and can make an unrelated lane appear unhealthy when HEAD
support regresses.

No audited probe deliberately asserts HEAD support. Each probe below now issues GET, discarding
the response body where only status or headers are needed. HEAD behavior is asserted directly by
the fast `HeadRequestMiddlewareTests` suite instead. The audit found no matching probes in
workflow files.

| Location | Endpoint | Probe purpose | Resolution |
|---|---|---|---|
| `docker-compose.yml` | `/healthz/live` | Server container liveness | `wget` GET; discard body |
| `docker-compose.yml` | `/operate` | Operations UI availability | `wget` GET; discard body |
| `docker-compose.gp-dev.yml` | `/healthz/live` | Server container liveness | `wget` GET; discard body |
| `docker/scale-test/compose.yml` (server) | `/healthz/live` | Server container liveness | `wget` GET; discard body |
| `docker/scale-test/compose.yml` (server replica) | `/healthz/live` | Replica container liveness | `wget` GET; discard body |
| `docker/scale-test/compose.yml` (proxy) | `/healthz/live` | Proxied server liveness | `wget` GET; discard body |
| `docs/guides/deploy/docker-compose.md` (server example) | `/healthz/live` | Documented server liveness | `wget` GET; discard body |
| `docs/guides/deploy/docker-compose.md` (operations example) | `/operate` | Documented operations UI availability | `wget` GET; discard body |
| `scripts/scale/scale-test.sh` | `/rest/services/1/FeatureServer` | Compare ETags across requests | `curl` GET; emit headers and discard body |
| `scripts/cloud/post-deployment-verification.sh` (security) | `/healthz/live` | Inspect security headers | `curl` GET; emit headers and discard body |
| `scripts/cloud/post-deployment-verification.sh` (version) | `/healthz/live` | Inspect version header | `curl` GET; emit headers and discard body |

The client-compat compose probe was already converted to an explicit GET by #3389. It remains
documented inline because its comment explains why `--spider` must not be restored.
