# Deploy rollback: the recovery fence

*Internal admin-API contract. Implemented by honua-server#4958; consumed by honua-devops#191.*

## What this is

`POST /api/v1/admin/deploy/operations/{operationId}/rollback` actuates a compensation against a live
serving target. Before #4958 the only thing standing between a credential and that compensation was
`PlatformDeployAuthority` (honua-server#4943), which answers *"may this principal reach the deploy
surface at all"*. It cannot answer *"is this the one recovery that was preauthorized"*, and nothing else
did: the request body carried only `reason`, so a rollback naming a different target, a revision that was
never serving, a foreign actor and tenant, an unknown grant and an expiry in 1999 returned **HTTP 200**
and settled the operation with every field silently dropped.

The **recovery fence** closes that. A protected activation now seals a grant at the moment of exposure,
publishes its terms on the operation, and refuses — at admission, before anything durable happens — any
rollback whose declared terms do not match.

## The sealed grant

When a candidate is first exposed to traffic, `deploy.protection` is created and never re-derived. It now
carries, in addition to the pre-existing revision/deadline/digest fields:

| field | meaning |
| --- | --- |
| `grantId` | Stable identity of this activation's recovery grant. Derived from the operation, target, prior+candidate revision pair, policy digest and exposure instant, so re-activating the same revisions mints a *different* grant. |
| `actor` | Principal that requested the protected activation. The only principal the grant authorizes. |
| `tenantId` | That principal's tenant binding, read from the validated identity. Absent on a single-tenant installation. |
| `permittedCompensation` | The single compensation this window preauthorizes. Today always `restore-previous-revision`. |

A client reads these from `GET /api/v1/admin/deploy/operations/{operationId}` and quotes them back. It
never invents them.

## The fence body

Every property below is optional. **A body that supplies none of them behaves exactly as it did before
#4958**, which is what keeps existing callers and single-tenant installations working. A body that
supplies any of them is asserting a grant, and every supplied term is enforced — an unsatisfied term is a
refusal, never a no-op.

```jsonc
POST /api/v1/admin/deploy/operations/{operationId}/rollback
{
  "reason": "telemetry breach on p99 latency",

  "targetId": "probe-target",                       // must equal the operation's target
  "expectedCandidateRevision": "rev-candidate-3",   // must equal protection.candidateRevision
  "expectedPreviousRevision": "rev-prior-1",        // must equal protection.previousRevision
  "expectedProtectionPhase": "observing",           // must equal protection.phase
  "grantId": "grant-...",                           // must equal protection.grantId
  "policyDigest": "A1B2...",                        // must equal protection.policyDigest
  "actor": "ops-agent",                             // must equal the authenticated principal
  "tenantId": "tenant-a",                           // must equal the caller's validated tenant
  "notAfter": "2026-09-16T06:10:00Z",               // refused once the server clock is past it
  "compensation": "restore-previous-revision"       // must equal protection.permittedCompensation
}
```

`actor` and `tenantId` are **cross-checks, not assertions**. They are compared against the validated
identity; the request body can never *establish* either one. Declaring `"actor": "someone-else"` is a
refusal, not an impersonation.

Any property not listed above is refused. The request type disallows unmapped members, so a client that
sends `expectedCurrentRevision` (a plausible-looking name this server does not implement) is told so
rather than having its fence silently dropped.

## Refusals

Every refusal carries a stable `code` in the problem document and leaves the operation untouched — no
status transition, no caller-supplied reason durably recorded.

| code | status | raised when |
| --- | --- | --- |
| `recovery_fence_unknown_property` | 400 | the body carried a property this server does not implement, or could not be read |
| `recovery_fence_protection_phase_unrecognized` | 400 | `expectedProtectionPhase` is not one of `observing`/`protected`/`recovering`/`expired`/`unavailable` |
| `recovery_fence_actor_mismatch` | 403 | the caller is not `protection.actor`, or the declared `actor` is not the caller |
| `recovery_fence_tenant_mismatch` | 403 | the caller's tenant is not `protection.tenantId`, or the declared `tenantId` is not the caller's |
| `recovery_fence_compensation_not_permitted` | 403 | `compensation` is not `protection.permittedCompensation` |
| `recovery_fence_target_mismatch` | 409 | `targetId` is not the operation's target |
| `recovery_fence_protection_window_absent` | 409 | grant terms were declared but the operation has no protection window |
| `recovery_fence_protection_phase_mismatch` | 409 | `expectedProtectionPhase` is not the phase the operation is in |
| `recovery_fence_candidate_revision_mismatch` | 409 | `expectedCandidateRevision` is not the activated revision |
| `recovery_fence_previous_revision_mismatch` | 409 | `expectedPreviousRevision` is not the revision the compensation restores |
| `recovery_fence_grant_mismatch` | 409 | `grantId` is not the grant this activation sealed |
| `recovery_fence_policy_digest_mismatch` | 409 | `policyDigest` is not the digest the activation was approved under |
| `recovery_fence_expired` | 412 | the server clock is past `notAfter` |

Identifier comparisons are ordinal case-insensitive after trimming: a difference in case or padding is a
client formatting artifact, not a different grant.

## Identity binding is not opt-in

The actor and tenant checks against the *sealed* pair run whether or not the caller supplied a fence.
Once an activation has recorded an actor, only that actor — or a principal holding one of the configured
platform-administrator roles (`MultiTenancy:MultiTenantAdminRoles`) — may actuate its compensation. A
foreign principal cannot escape the binding by staying silent.

On a single-tenant installation (`MultiTenancy:Enabled=false`) no tenant is ever resolved or recorded, so
the fence there is purely actor-bound and no rollback is ever refused for a tenant reason.

## Ordering, and why admission is the only correct layer

The fence is evaluated after the operation is located (a missing operation is still `404`) and **before**
the approval gate and before the operation is submitted to the invoker. That ordering is the point: on the
pinned candidate the eventual `ManualInterventionRequired` came from the GitOps hand-off backend reporting
it could not actuate a rollback out of band — *after* the request had been authorized, the operation had
transitioned and an attacker-supplied reason had been durably recorded. A fence that runs after admission
is not a fence.

## Consumer

honua-devops#191's recovery grant seals actor / tenant / target / prior+candidate revisions / policy
digest / expiry / permitted compensation locally. Those fields map one-to-one onto the body above, so the
agent's local refusal and the server's refusal now agree, and "only that declared recovery is
preauthorized" is a server-enforced boundary rather than an in-process one.
