# Upgrade and rollback

1. Confirm a current database backup; privately back up file storage, `.env`
   and `secrets/`. The executor's database backup does not cover file storage.
2. Read `honua_platform_release_status` on the management endpoint.
3. Set `HONUA_RELEASE_IMAGE`, `HONUA_RELEASE_WORKER_IMAGE` and
   `HONUA_RELEASE_VERSION` in `.env` from the target release lock without
   displaying the file. Pull the target serving image by its immutable digest.
4. Run `python3 quickstart.py` from the package directory to converge management
   services. It preserves the serving image until approval. Verify that the
   management status's declared `releaseVersion` matches the requested target.
5. Call `honua_propose_platform_release_convergence` using the discovered schema
   and target version. A separate approver reviews and approves the proposal.
   Poll its returned proposal URI until `Completed` or `Failed`.
6. For rollback, call `honua_propose_rollback`. Inspect its `diff` and `dryRun`,
   especially the selected revision and backup, before approval. Never guess a
   prior image's maximum schema; obtain it from that exact image's metadata.
7. Read release status again. Check `observedArtifactReference` against the
   target image pin, `skewed: false`, and `isCoVersioned: true`. The local
   `observedImageId` is not the release image reference. Use `deploymentRevision`
   or image digest to identify the running build, rather than a version label.

Never run `docker compose down --volumes` or remove backups as recovery. A
failed proposal requires diagnosis; it is not permission to edit the serving
image or restart it outside the approval flow.
