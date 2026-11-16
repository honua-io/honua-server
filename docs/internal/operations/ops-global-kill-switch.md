# Stop autonomous operations on ECS

The admin autonomy settings update controls both the durable server kill switch and
an explicitly configured ECS watchdog mode parameter. Server credentials use the
ECS task role. Grant `ssm:PutParameter` only for that watchdog parameter; the server
does not discover parameters or change task definitions.

Set these deployment environment variables when the watchdog is installed:

| Setting | Value |
| --- | --- |
| `Observability__OpsFindings__Watchdog__ModeParameterName` | The existing watchdog mode parameter path |
| `Observability__OpsFindings__Watchdog__ResumeMode` | `Act`, `NotifyOnly`, or `Off`; default `Act` |

Without a parameter path, existing server-only controls remain available and no SSM
client is registered. Unsupported resume modes fail configuration rather than
silently enabling actions. A configured server autonomy kill switch keeps the
watchdog resume mode `Off`.

Stopping first commits the durable server stop, then writes watchdog mode `Off`
with its own ten-second timeout, so a client that disconnects after the commit
does not leave the watchdog acting. The watchdog stop is also sent when the durable
write reports a failure, because the stop may already have committed. If the parameter write fails, the API reports service unavailable and the server
remains stopped. Restoring first writes the configured watchdog resume mode, then
clears the durable server stop. A failed parameter write leaves the server stopped.
If restore fails before the durable write, including a lost parameter response, the
server suspends the watchdog again and leaves the original stop record and its author
unchanged. If it fails during or after the durable write, including cancellation after
a database commit, the server also reasserts the durable stop. Each compensation uses a separate five-second timeout independent of
the original request. Both are attempted even if one fails. A failed compensation is reported as service
unavailable; retry the requested setting after checking both controls.
Updates across ECS tasks serialize under a PostgreSQL transaction advisory lock.
Error responses do not expose SDK or database exception details.

At startup the server reapplies a durable or configured stop to the watchdog,
retrying every failure every five seconds without stopping the host. The
configuration kill switch applies to the task that sets it and is never written
to the shared database, so removing it needs no admin restore and does not
overwrite who set a manual stop. Startup never resumes the watchdog automatically,
so a manually selected `Off` or `NotifyOnly` mode remains effective until an
explicit restore. Watchdog actions already accepted by ECS cannot be cancelled
by a later mode change. The watchdog rechecks its mode before reserving an action.

A failed stop is a partial stop: keep the server flag enabled, set the existing
watchdog parameter to `Off` through the deployment's operator workflow, and retry
the admin settings update after fixing permissions or connectivity. No successful
response is reported until both control paths complete.
