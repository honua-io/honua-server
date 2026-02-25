# Honua Python SDK (Scaffold)

This directory contains an initial scaffold for the Honua Python SDK workstream
tracked by GitHub issue #323.

The current scope is intentionally minimal:

- a synchronous `HonuaClient`,
- task-oriented methods for `query`, `edit`, `admin/health`, and `map export`,
- basic auth header support (`X-API-Key`, `Authorization: Bearer ...`),
- unit tests using `httpx.MockTransport`.

## Status

This package is an early scaffold and not yet published.

## Local Development

```bash
python3 -m pytest sdk/python/tests -q
```

## Example

```python
from honua_sdk import HonuaClient

with HonuaClient("http://localhost:8080") as client:
    services = client.list_services()
    features = client.query_features("default", 1, where="1=1")
```
