# AI workflow semantic alignment review

Review date: 2026-10-16 Hawaii time; public artifact probes ran on
2026-10-17 UTC. This is a bounded source and documentation review, with live
artifact-availability checks. It is not end-to-end release qualification.

## Acceptance goals

1. Help a human express GIS intent more precisely using shared concepts across
   infrastructure, data, services, analysis, map/app composition, and publication.
2. Help an agent execute that intent across tasks: bind actual resources and
   interfaces, preserve context, verify outcomes, and recover or resume correctly.

The first evaluation starts with “Set up Honua in Docker on this machine.”
Local prerequisites are discovered before questions about missing user choices.
Cloud is a later target, not a prerequisite for evaluating the platform.
Studio is realtime composition; Git is optional. Publishing a saved map back
to Honua completes a different step from saving or previewing it.

## Reviewed baselines

| Repository | Source revision | Scope |
| --- | --- | --- |
| honua-server | `a2ff8bb004` | Public OKF concepts and workflows, validators, install guides, Studio server contracts and tests |
| honua-studio | `8e3d654` | Realtime composition and publication adapter contracts |
| honua-devops | `62287ff96e78525aee03d665454fd387d4a05b98` | Current fetched trunk, provisioning, prerequisites, handoff verification and tool ownership |

The documentation changes accompanying this review resolve the documentation
findings below. They do not change runtime policy, provisioning adapters, or
browser publication code.

## What determines the correct behavior

Product intent defines the desired experience. Published schemas and installed
tool descriptors define callable interfaces. Implementation and independent
observations establish what the selected version actually does. A discrepancy
requires a documentation correction, implementation fix, or both; runtime
behavior is evidence, not automatic approval of the product design.

Human explanations and agent context must derive from the same OKF source.
`honua.gis-workflow/v1` is the semantic planning profile; Honua Spec and workflow
packages are separate executable contracts. The semantic validator checks
document composition and deliberately returns `executionVerified: false`.
The proposed refinement types and outcome predicates in
[semantic contracts](../../concepts/domain/semantic-contracts.md) are not a
shipped universal execution/verifier layer.

## Findings and disposition

| Finding | Evidence | Disposition |
| --- | --- | --- |
| The first install cannot discover tools from a server that does not exist. A published layer is not needed to connect an agent. | Install workflow and MCP prerequisite descriptions | Corrected; host bootstrap precedes server discovery, then first dataset publication. |
| The packaged quickstart advertises unavailable release artifacts. | Anonymous Docker pull of `ghcr.io/honua-io/honua-server:2026.1-rc`: `not found`; HTTPS GET of `https://honua.io/data/customer-install-manifest.json`: 404 | Added dated availability notice to package entrypoints. Release publication and full replay remain required. |
| A “propose publication” tool can publish immediately for an admin. Declaring only a proposal understates its effect. | [Server tool](../../../src/Honua.Ai/Features/Protocols/Mcp/Mcp/Studio/StudioProposePublicationTool.cs), [admin/non-admin tests](../../../tests/dotnet/Honua.Server.Tests/Features/Studio/StudioAdminMcpPublicationTests.cs) | Corrected workflow effect, recovery contract and both result branches. Pending approval is not publication. Tests inspected, not run. |
| Browser publication models only `AwaitingApproval`, while server supports direct `Published`. | Studio `src/mcp/studio-tools.ts` and `src/chat/share-preview.ts` at reviewed baseline | Narrowed browser documentation; adapter implementation gap remains. |
| Enterprise default guardrail can require approval for ordinary Studio draft composition. | [Guardrail](../../../src/Honua.Core/Features/Guardrails/DefaultGuardrailLadder.cs), [tests](../../../tests/dotnet/Honua.Core.Tests/Features/Guardrails/DefaultGuardrailLadderTests.cs) | Documented current limitation against realtime product intent. No policy change made. |
| Old standalone Studio docs understated existing container/configuration work. | Studio build/release sources and self-hosted guide | Corrected to source build or verified release, preserving Preview status and separating packaging from publication. |
| Human and agent entrypoints did not present the complete journey coherently. | Documentation index, workflow catalog, domain concepts | Added AI getting-started and semantic-language walkthroughs; expose local prerequisites, publication back to Honua, optional Git, and task handoffs. |
| Git workspace language could imply Git was required or already shipped in Studio. | Intent interpretation and workspace concept | Clarified optional integration and existing reducer/draft history versus proposed source-asset adapter. |
| Workflow reference omitted supported SDK interfaces and mandatory recovery details. | `scripts/ci/gis_workflow.py` | Aligned field/target/interface documentation with the validator. |
| General cloud support could be mistaken for provisioning-tool support. | DevOps `ProvisioningStackCatalog` and `HonuaOperationsToolkit.Provisioning.cs` on fetched trunk | Guide distinguishes infrastructure deployment options from current `aws-ecs` provisioning support. |

## Existing DevOps work to reuse

At the reviewed trunk, `honua-devops` already supplies
`provision_infrastructure`, `install_handoff`, and `verify_install_handoff`.
The verifier checks package integrity, server readiness and identity,
authentication, MCP initialization, paginated discovery, and an admin status
call. Emitting connection settings is explicitly distinct from verifying them.
See its [handoff verification contract](https://github.com/honua-io/honua-devops/blob/62287ff96e78525aee03d665454fd387d4a05b98/docs/install-handoff-system-verification.md).

Its `--preflight` checks Terraform configuration and an existing Honua/OTEL
backend. The reviewed provisioning catalog allows `aws-ecs`; no local Docker
bootstrap/prerequisite adapter was found in the tool catalog, provisioning
implementation, scripts or docs. A host agent can follow the public Compose
procedure today, but that is not evidence of a DevOps Docker actuator.

Day-2 observation and operational proposals have moved to the installed server's
MCP surface. Do not revive retired DevOps diagnostic/upgrade tools from a stale
checkout. Likewise, DevOps generic rollback is experimental/off; qualified
server-triggered recovery is separately scoped to the actual target and revision.

## Validation and remaining proof

Public checks used an empty Docker client configuration: no registry credentials.
Both pinned Python packages (`honua-admin` 0.1.12, `honua-sdk` 0.1.16) returned
HTTP 200 from PyPI. The GHCR and Docker Hub nightly image manifests were
retrievable. No nightly was substituted for the unavailable release candidate;
no stack was started and no installation, query, map, or restart pass is claimed.

Documentation validation covers OKF structure, workflow bindings, generated
graph/index freshness, local links, and the embedded language example. It cannot
establish an agent completed the journey. The next live evaluation must record
the exact published image and client artifacts, host preflight, authenticated
handoff, published sample query, persistence after restart, rendered composition,
save/reopen and publication access. Preserve failures and distinguish agent
interpretation errors from independently reproduced product or infrastructure
failures.

The two acceptance goals need separate outcomes: a person can express the right
objects and constraints without tool syntax; an agent can preserve that meaning
through execution and task handoff. A readable guide alone proves neither.
