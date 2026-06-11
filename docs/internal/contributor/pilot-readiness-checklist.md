# Alpha/Beta Pilot Readiness Checklist

Use this checklist before starting an alpha or beta pilot from the quality-sweep
train. It turns issue
[#1601](https://github.com/honua-io/honua-server/issues/1601) into a concrete
validation gate with owners, required evidence, and go/no-go criteria.

This is a release-owner checklist, not the operator onboarding runbook. Runtime
prerequisites and first-hour failure modes are tracked separately in
[#1600](https://github.com/honua-io/honua-server/issues/1600).

## Scope

In scope:

- `honua-server` validation after the quality sweep merges.
- Nightly/manual gates that are not covered by routine PR CI.
- Evidence links needed for alpha/beta pilot approval.
- Cross-repo readiness references for RC validation and performance baselines.

Out of scope:

- Changing Docker Compose defaults or quickstart compose behavior.
- Replacing the [release checklist](RELEASE_CHECKLIST.md).
- Customer-specific onboarding steps, secrets, datasets, or support contacts.

## Required Inputs

Record these before evaluating the gates:

| Input | Required value |
|---|---|
| Candidate server ref | Full commit SHA on `trunk` after the quality-sweep train lands |
| Quality-sweep PRs | [#1563](https://github.com/honua-io/honua-server/pull/1563) merged; [#1599](https://github.com/honua-io/honua-server/pull/1599) merged or explicitly waived |
| Coordinated follow-ups | [#1593](https://github.com/honua-io/honua-server/issues/1593) reviewed for pilot-impacting limitations |
| Pilot type | `alpha` or `beta` |
| Decision owner | One named owner who can accept or block the pilot |
| Evidence packet | One issue comment, project update, or release-gate artifact that links every run below |

## Validation Gates

Every gate needs a direct artifact link, a result, an owner, and a timestamp.
Do not rely on "latest run looked green" without recording the run URL and the
server ref that was tested.

| Gate | Owner | Required evidence | Pass condition |
|---|---|---|---|
| Train merged | Server owner | PR links for #1563 and #1599, plus candidate `trunk` SHA | Required PRs are merged, or #1599 has a written waiver that lists remaining risk |
| PR and trunk health | Server owner | Green CI on the merge PRs and green `trunk-sanity.yml` on the candidate SHA | No failed required PR gates; no blocked PR-template compliance checks |
| Full integration lane | Server owner | Manual or scheduled `ci.yml` full run on the candidate SHA | Full configured matrix is green, or each failure is a documented flake with rerun evidence |
| CITE evidence | Protocol owner | `cite-evidence-report.yml` run URL and refreshed [CITE status](../../cite-status.md) when totals change | Evidence bundle reports all suites passed with zero failed, skipped, or CantTell results |
| Scale validation | Platform owner | Local or CI transcript for the 3-replica scale stack and `Category=Scale` suite | Multi-node routing/cache/failover checks pass; any Redis or replica limitation is accepted in writing |
| Load/performance baseline | Performance owner | `load-soak-nightly.yml` run URL and geobench baseline link or waiver tracked by [#1596](https://github.com/honua-io/honua-server/issues/1596) | Beta requires a recorded baseline; alpha may proceed with a bounded waiver and no public performance claims |
| Security nightly | Security owner | `security-nightly.yml` run URL and artifacts | No unresolved high or critical dependency/container findings |
| Real-client compatibility | Compatibility owner | `client-interop-nightly.yml`, `windows-client-compat-nightly.yml`, and SDK compatibility run URLs | No baseline pass regressions, missing expected evidence, or new unbaselined failures affecting pilot clients |
| RC validation | DevOps owner | [honua-devops#41](https://github.com/honua-io/honua-devops/issues/41) evidence bundle or `release-bundle.yml` artifact | Candidate image, manifest, environment, pass/fail checks, and owning follow-ups are recorded |
| Pilot expectations | Product owner | Pilot expectations note or signed-off issue comment | MVP deferrals and accepted #1593 limitations are visible to the pilot owner before kickoff |

## Owner Commands

Run heavyweight workflows in GitHub Actions where possible. These commands
dispatch the relevant manual lanes from the candidate branch or `trunk`.

```bash
gh workflow run ci.yml --repo honua-io/honua-server --ref trunk
gh workflow run cite-evidence-report.yml --repo honua-io/honua-server --ref trunk -f publish_pages=false
gh workflow run security-nightly.yml --repo honua-io/honua-server --ref trunk -f include_transitive=true
gh workflow run load-soak-nightly.yml --repo honua-io/honua-server --ref trunk -f profile=nightly
gh workflow run client-interop-nightly.yml --repo honua-io/honua-server --ref trunk
gh workflow run windows-client-compat-nightly.yml --repo honua-io/honua-server --ref trunk
gh workflow run sdk-server-compatibility.yml --repo honua-io/honua-server --ref trunk -f server_current_ref=<candidate-sha>
gh workflow run release-bundle.yml --repo honua-io/honua-server --ref trunk -f release_id=honua-YYYY-MM-preview -f channel=preview -f server_ref=<candidate-sha> -f run_integration=true -f run_sdk=true -f promote=false
```

For local multi-node validation, use the supported scale-test entrypoint and
the scale test suite. The `dotnet` shim applies the shared build/test lock.

```bash
./scripts/scale/scale-test.sh --test all

HONUA_SCALE_TEST_BASE_URL=http://localhost:8080 \
HONUA_SCALE_TEST_REDIS=localhost:6379 \
HONUA_SCALE_TEST_ADMIN_API_KEY=scale-test-admin-password \
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --filter Category=Scale
```

If the default ports are occupied, set the documented overrides before starting
the stack:

```bash
export HONUA_SCALE_TEST_HTTP_PORT=18080
export HONUA_SCALE_TEST_REDIS_PORT=6380
export HONUA_SCALE_TEST_POSTGRES_PORT=55434
```

## Evidence Packet Template

Use this template in #1601, the roadmap project, or the release gate. The packet
is complete only when every required link is immutable enough for later audit.

```markdown
## Pilot readiness evidence

- Candidate server ref:
- Pilot type: alpha | beta
- Decision owner:
- Decision date:

| Gate | Result | Evidence link | Owner | Notes |
|---|---|---|---|---|
| Train merged | pass/fail/waived |  |  |  |
| PR and trunk health | pass/fail/waived |  |  |  |
| Full integration lane | pass/fail/waived |  |  |  |
| CITE evidence | pass/fail/waived |  |  |  |
| Scale validation | pass/fail/waived |  |  |  |
| Load/performance baseline | pass/fail/waived |  |  |  |
| Security nightly | pass/fail/waived |  |  |  |
| Real-client compatibility | pass/fail/waived |  |  |  |
| RC validation | pass/fail/waived |  |  |  |
| Pilot expectations | pass/fail/waived |  |  |  |

### Accepted limitations

-

### Go/no-go decision

Decision: go | no-go
Reason:
```

## Go/No-Go Rules

Alpha can start only when:

- The quality-sweep train is merged or remaining train work has an explicit
  owner-approved waiver.
- CITE, security, full integration, and scale gates are green on the candidate
  SHA, or each exception has a bounded waiver with an owning issue.
- Any #1593 deferral that affects the pilot path is listed as an accepted
  limitation before the pilot starts.
- The pilot expectations note states the MVP operational deferrals: edge rate
  limiting, secure-connection audit history, audit-log storage, and compliance
  dashboards are not app-level MVP features.

Beta adds these stricter requirements:

- No unresolved waiver for CITE, security high/critical findings, or RC image
  validation.
- A geobench or equivalent release-keyed performance baseline is recorded.
- Multi-node durability risks that affect the beta scenario are either fixed or
  removed from the beta scope in writing.
- Real-client compatibility evidence covers the clients named for the beta.

No-go if any of these are true:

- The candidate ref is not a `trunk` commit after the train merges.
- A validation gate has no owner, timestamp, or artifact link.
- A failed nightly/manual gate is described only as "probably flaky" without a
  rerun URL or linked investigation.
- Pilot-facing limitations differ from the MVP deferrals or #1593 status.
- RC validation cannot identify the exact image digest and environment checked.

## Source Links

- [CI gate model](../ci/gate-model.md)
- [CI workflow inventory](../ci/workflow-inventory.md)
- [CI quality gates](CI_QUALITY_GATES.md)
- [Release checklist](RELEASE_CHECKLIST.md)
- [Release bundle](release-bundle.md)
- [CITE runbook](cite-runbook.md)
- [OGC CITE conformance evidence](ogc-cite-conformance-evidence.md)
- [CITE status](../../cite-status.md)
- [Evidence index](../evidence/README.md)
- [Migration performance evidence](../evidence/migration-performance-evidence.md)
- [Scale and tune performance](../../guides/deploy/scaling-and-performance.md)
- [Scale-test Docker assets](../../../docker/scale-test/README.md)
