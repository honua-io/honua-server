# Upgrade and rollback

Follow the [upgrade and rollback procedure](https://honua.io/docs/guides/connect/working-with-ai-agents/)
and [release guide](https://honua.io/docs/guides/deploy/upgrade-and-rollback/).
Use the installed management endpoint's live schemas. Retain the proposal id,
selected revision, backup identity and immutable target pin for the readback.
Require a separate approver, then verify the observed artifact against that pin;
a submitted request or a local image id does not prove convergence.
Never guess a prior image's maximum schema; obtain it from that exact image's metadata.

Never run `docker compose down --volumes` or remove backups as recovery. A
failed proposal requires diagnosis; it is not permission to edit the serving
image or restart it outside the approval flow.
