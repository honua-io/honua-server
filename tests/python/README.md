# Python Integration Tests

Documentation has moved to [testing-python.md](../../docs/internal/contributor/testing-python.md).

### Shared-host process isolation

Locally launched fixture servers require Linux procfs. Readiness checks the
spawned process and its descendants against every listening socket on the
selected port, before and after the health request. An exited process, foreign
listener or unavailable ownership proof fails startup; a health response alone
cannot make a fixture ready. Failed launches stop their own process session.

Outside CI, unset ports select a free ephemeral port. In CI, existing defaults
and worker offsets remain. Override `HONUA_TEST_PORT` for the shared/JS harness,
`HONUA_PYQGIS_PORT` for PyQGIS, and `HONUA_STAC_COMPAT_PORT` plus
`HONUA_STAC_COMPAT_AUTH_PORT` for the anonymous/authenticated STAC pair. Set zero
to request an ephemeral port explicitly. Allocation does not reserve the socket
until launch; an intervening bind fails ownership verification safely.
