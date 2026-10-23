# Issue 5625 audit disposition

Re-verification was performed against the current worktree at `7c422ec`. Work was
limited to the highest-severity finding and the small, directly related control-plane
fixes that could be demonstrated without Docker. The remaining findings are recorded
without speculative changes so that later fix units can begin with a failing test.

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-INF-004` | fixed | `SRV_INF_004_CoordinatedRelease_TenantBoundAdmin_IsDeniedBeforeControlPlaneAccess` covers create, get/reconcile, approve, and rollback. Each handler now invokes `PlatformDeployAuthority.Deny` before reading or mutating control-plane state. The published `control-plane.deploy.rollback` and `control-plane.coordinated-release.rollback` operations enforce the same rule in the shared `WorkflowRollbackOperationExecutor`, so `/api/v1/operations/{id}/submit`, MCP and approval replay cannot bypass it (`CanonicalRollbackExecutor_TenantBoundAdmin_IsDeniedBeforeWorkflowAccess`). |
| `SRV-INF-015` | fixed | `SRV_INF_015_AdminApiLoopback_PreservesPublicHostHeader` proves that the internal request preserves the authenticated request's public `Host` header. This changes the loopback request metadata but not the public wire format. |
| `SRV-AUTH-013` | not attempted | Re-verified in `NetworkTopologyRebuildSubmissionService.SubmitAsync`: `CreateAttemptAsync` still precedes `jobStore.TryCreateAsync`, and this fix unit prioritized the platform-admin boundary. |
| `SRV-GP-002` | not attempted | Re-verified in `GeoprocessingJobDispatcher.EnsureAdmittedAsync`: `PrincipalId` is still populated from `principal.Identity?.Name`. |
| `SRV-GS-005` | not attempted | Re-verified in `FeatureServerEditsHandler.ResolveEditPrincipal`: edit identity still uses `httpContext.User.Identity.Name`. |
| `SRV-INF-016` | fixed | `SRV_INF_016_ApproveGate_DuringRollback_IsRejectedWithoutChangingState` proves stale approval cannot move rollback back to reconciliation. Approval now requires the exact awaiting gate and uses a compare-and-set write. The observable change is that stale or racing approvals are rejected rather than reviving an operation. |
| `SRV-INF-019` | not attempted | Re-verified in `OperationDispatcher.SubmitAsync`: supported dry runs still synthesize a completed handle without invoking the executor's declared dry-run route. |
| `SRV-INF-020` | not attempted | Re-verified in `AdminConnectImportOperationExecutor.GetStatusAsync`: status is still copied from the existing handle without querying the import job. |
| `SRV-INF-021` | not attempted | Re-verified in `OperationGateway.PersistResolutionAsync`: a rejection CAS retry still refuses only terminal states, so it can refresh from `Executing`. |
| Topology promotion replay request binding | not attempted | Re-verified in `PostgresNetworkTopologyPromotionStore.TryGetIdempotentReplayAsync`: replay lookup is still keyed only by dataset and idempotency key. This S3 item was left after higher-severity work. |
| Owner-field reassignment | not attempted | Re-verified in `FeatureServerEditsHandler`: owner authorization still resolves the existing owner independently of the caller-supplied merged attributes. This S3 item was left after higher-severity work. |
| Case-insensitive inline-password rejection | not attempted | Re-verified in `AdminConnectImportOperationExecutor.ValidateAsync` and `AdminConnectImportApprovalPayload.From`: both still use an ordinal lookup for the literal `password` key. This S3 item was left after higher-severity work. |
